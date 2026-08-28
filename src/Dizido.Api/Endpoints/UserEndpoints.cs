using Dizido.Api.Auth;
using Dizido.Contracts.Users;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

internal static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/users").WithTags("Users");

        // O POST que criava usuário sem senha morreu aqui: quem cria conta agora é
        // POST /api/auth/register, que passa pelo Identity e exige credenciais.

        group.MapGet("/me", async (
            ICurrentUser currentUser,
            DizidoDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me, ct);

            return profile is null ? Results.NotFound() : Results.Ok(profile.ToResponse());
        });

        group.MapGet("/{id:guid}", async (Guid id, DizidoDbContext db, CancellationToken ct) =>
        {
            // AsNoTracking: esta consulta é só leitura, então não faz sentido o DbContext
            // guardar snapshots das entidades para detectar alterações. Menos memória e
            // menos trabalho por requisição.
            var user = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

            return user is null ? Results.NotFound() : Results.Ok(user.ToResponse());
        });

        group.MapGet("/", async (DizidoDbContext db, CancellationToken ct) =>
        {
            var users = await db.Profiles
                .AsNoTracking()
                .OrderBy(u => u.DisplayName)
                .Select(u => new UserResponse(u.Id, u.DisplayName, u.AvatarUrl, u.CreatedAt, u.LastSeenAt))
                .ToListAsync(ct);

            return Results.Ok(users);
        });

        return group;
    }

    internal static UserResponse ToResponse(this UserProfile user) =>
        new(user.Id, user.DisplayName, user.AvatarUrl, user.CreatedAt, user.LastSeenAt);
}
