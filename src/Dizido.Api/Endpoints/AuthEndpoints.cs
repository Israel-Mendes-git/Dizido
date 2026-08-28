using Dizido.Api.Auth;
using Dizido.Contracts.Auth;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Identity;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dizido.Api.Endpoints;

internal static class AuthEndpoints
{
    private const string RefreshCookieName = "dizido_refresh";

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<DizidoUser> users,
            DizidoDbContext db,
            IAccessTokenService tokens,
            IOptions<JwtOptions> jwt,
            TimeProvider clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7(now);

            var account = new DizidoUser
            {
                Id = id,
                UserName = request.Email,
                Email = request.Email,
                CreatedAt = now,
            };

            // O UserManager cuida do hash da senha (PBKDF2 com salt por usuário) e das
            // validações configuradas. Nunca escreva PasswordHash à mão.
            var result = await users.CreateAsync(account, request.Password);

            if (!result.Succeeded)
            {
                return Results.ValidationProblem(
                    result.Errors.GroupBy(e => e.Code)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
            }

            // Perfil e conta compartilham o Id. Se a criação do perfil falhar, a conta ficaria
            // órfã — por isso o SaveChanges vem logo em seguida e a FK 1:1 garante o vínculo.
            db.Profiles.Add(UserProfile.Create(id, request.DisplayName, now));
            await db.SaveChangesAsync(ct);

            return Results.Ok(await IssueSessionAsync(
                account, request.DisplayName, db, tokens, jwt.Value, now, http, ct));
        });

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<DizidoUser> users,
            DizidoDbContext db,
            IAccessTokenService tokens,
            IOptions<JwtOptions> jwt,
            TimeProvider clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var account = await users.FindByEmailAsync(request.Email);

            // Mensagem idêntica para "email não existe" e "senha errada", de propósito.
            // Respostas diferentes permitiriam descobrir quais emails têm conta no serviço
            // (enumeração de usuários) — informação útil para phishing dirigido.
            if (account is null || !await users.CheckPasswordAsync(account, request.Password))
            {
                return Results.Problem(
                    title: "Credenciais inválidas",
                    detail: "Email ou senha incorretos.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var now = clock.GetUtcNow();

            var profile = await db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == account.Id, ct);

            return Results.Ok(await IssueSessionAsync(
                account, profile?.DisplayName ?? account.Email!, db, tokens, jwt.Value, now, http, ct));
        });

        group.MapPost("/refresh", async (
            UserManager<DizidoUser> users,
            DizidoDbContext db,
            IAccessTokenService tokens,
            IOptions<JwtOptions> jwt,
            TimeProvider clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var presented = http.Request.Cookies[RefreshCookieName];

            if (string.IsNullOrEmpty(presented))
            {
                return Unauthorized("Nenhum token de renovação foi apresentado.");
            }

            var hash = RefreshToken.Hash(presented);
            var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            var now = clock.GetUtcNow();

            if (stored is null)
            {
                return Unauthorized("Token de renovação desconhecido.");
            }

            // Token já revogado sendo apresentado: ou o cliente perdeu a resposta da renovação
            // anterior, ou alguém roubou o token. Como não dá para distinguir, tratamos como
            // roubo — derrubar a sessão é um incômodo; deixar o invasor dentro, não.
            if (!stored.IsActive(now))
            {
                await RevokeFamilyAsync(db, stored.UserId, now, ct);
                return Unauthorized("Token de renovação reutilizado. Todas as sessões foram encerradas.");
            }

            var account = await users.FindByIdAsync(stored.UserId.ToString());
            if (account is null)
            {
                return Unauthorized("Conta não encontrada.");
            }

            var profile = await db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == account.Id, ct);

            // Rotação: o token usado morre e um novo nasce, encadeado ao anterior.
            var (fresh, plain) = RefreshToken.Create(account.Id, now, jwt.Value.RefreshTokenLifetime);
            stored.Revoke(now, fresh.Id);
            db.RefreshTokens.Add(fresh);
            await db.SaveChangesAsync(ct);

            WriteRefreshCookie(http, plain, fresh.ExpiresAt);

            return Results.Ok(new AuthResponse(
                tokens.Create(account, now),
                now.Add(jwt.Value.AccessTokenLifetime),
                account.Id,
                profile?.DisplayName ?? account.Email!));
        });

        group.MapPost("/logout", async (
            DizidoDbContext db,
            TimeProvider clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var presented = http.Request.Cookies[RefreshCookieName];

            if (!string.IsNullOrEmpty(presented))
            {
                var hash = RefreshToken.Hash(presented);
                var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
                stored?.Revoke(clock.GetUtcNow());
                await db.SaveChangesAsync(ct);
            }

            http.Response.Cookies.Delete(RefreshCookieName);

            // O access token que o cliente ainda tem continua válido até expirar (até 15 min).
            // É o preço de não consultar o banco a cada requisição. Para logout imediato de
            // verdade seria preciso uma lista de revogação em cache — troca justa apenas se o
            // requisito exigir.
            return Results.NoContent();
        });

        return group;
    }

    private static async Task<AuthResponse> IssueSessionAsync(
        DizidoUser account,
        string displayName,
        DizidoDbContext db,
        IAccessTokenService tokens,
        JwtOptions jwt,
        DateTimeOffset now,
        HttpContext http,
        CancellationToken ct)
    {
        var (refresh, plain) = RefreshToken.Create(account.Id, now, jwt.RefreshTokenLifetime);

        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        WriteRefreshCookie(http, plain, refresh.ExpiresAt);

        return new AuthResponse(
            tokens.Create(account, now),
            now.Add(jwt.AccessTokenLifetime),
            account.Id,
            displayName);
    }

    private static void WriteRefreshCookie(HttpContext http, string value, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(RefreshCookieName, value, new CookieOptions
        {
            // HttpOnly: JavaScript não enxerga este cookie. É a defesa contra XSS.
            HttpOnly = true,

            // Secure: só trafega em HTTPS. Em localhost o navegador abre exceção para http.
            Secure = true,

            // SameSite=Strict: o navegador não envia este cookie em requisições originadas
            // de outro site. É a defesa contra CSRF.
            SameSite = SameSiteMode.Strict,

            // Path restrito: o cookie só é enviado para os endpoints que precisam dele,
            // e não em toda requisição à API.
            Path = "/api/auth",

            Expires = expiresAt,
        });

    private static async Task RevokeFamilyAsync(
        DizidoDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var family = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in family)
        {
            token.Revoke(now);
        }

        await db.SaveChangesAsync(ct);
    }

    private static IResult Unauthorized(string detail) => Results.Problem(
        title: "Não autenticado",
        detail: detail,
        statusCode: StatusCodes.Status401Unauthorized);
}
