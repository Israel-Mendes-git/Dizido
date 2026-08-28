using Dizido.Domain.Enums;

namespace Dizido.Domain.Entities;

/// <summary>
/// Uma conversa: ou um privado entre duas pessoas (<see cref="ConversationType.Direct"/>),
/// ou um grupo (<see cref="ConversationType.Group"/>).
/// </summary>
/// <remarks>
/// Esta classe é a "raiz": toda alteração de membros passa por aqui, nunca mexendo na lista
/// por fora. É por isso que <see cref="Members"/> é somente-leitura e os métodos de
/// <see cref="ConversationMember"/> são <c>internal</c> — as regras de quem pode entrar,
/// sair ou virar admin ficam em um lugar só, e não espalhadas pelos endpoints.
/// </remarks>
public sealed class Conversation
{
    public const int MaxTitleLength = 60;

    private readonly List<ConversationMember> _members = [];

    private Conversation() { }

    public Guid Id { get; private set; }

    public ConversationType Type { get; private set; }

    /// <summary>Obrigatório em grupos, sempre nulo em conversas diretas.</summary>
    public string? Title { get; private set; }

    public string? AvatarUrl { get; private set; }

    public Guid CreatedById { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Momento da última mensagem. Campo desnormalizado: ele repete uma informação que dá
    /// para derivar da tabela de mensagens.
    /// </summary>
    /// <remarks>
    /// A duplicação é deliberada. A lista de conversas é ordenada por "atividade mais recente"
    /// e é a tela mais aberta do app inteiro. Sem este campo, montá-la exige varrer as mensagens
    /// de cada conversa para achar a mais nova — caro e proporcional ao histórico. Com ele, é
    /// um ORDER BY num índice. O preço é manter os dois em sincronia ao gravar mensagem.
    /// </remarks>
    public DateTimeOffset LastMessageAt { get; private set; }

    public IReadOnlyList<ConversationMember> Members => _members;

    public IEnumerable<ConversationMember> ActiveMembers => _members.Where(m => m.IsActive);

    /// <summary>Cria um privado entre duas pessoas. A ordem dos dois não importa.</summary>
    public static Conversation CreateDirect(Guid userA, Guid userB, DateTimeOffset now)
    {
        DomainException.Require(userA != userB, "Não é possível abrir um privado consigo mesmo.");

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(now),
            Type = ConversationType.Direct,
            CreatedById = userA,
            CreatedAt = now,
            LastMessageAt = now,
        };

        // Em privado ninguém é dono: os dois têm exatamente os mesmos poderes.
        conversation._members.Add(ConversationMember.Create(conversation.Id, userA, MemberRole.Member, now));
        conversation._members.Add(ConversationMember.Create(conversation.Id, userB, MemberRole.Member, now));

        return conversation;
    }

    public static Conversation CreateGroup(string title, Guid ownerId, DateTimeOffset now)
    {
        ValidateTitle(title);

        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(now),
            Type = ConversationType.Group,
            Title = title.Trim(),
            CreatedById = ownerId,
            CreatedAt = now,
            LastMessageAt = now,
        };

        conversation._members.Add(ConversationMember.Create(conversation.Id, ownerId, MemberRole.Owner, now));

        return conversation;
    }

    /// <summary>
    /// Adiciona alguém ao grupo. Devolve o aviso de sistema a ser gravado no fluxo.
    /// </summary>
    /// <param name="actorId">Quem está adicionando. Precisa ser admin ou dono.</param>
    public Message AddMember(Guid actorId, Guid userId, DateTimeOffset now,
        MemberRole role = MemberRole.Member)
    {
        DomainException.Require(
            Type == ConversationType.Group,
            "Conversa direta tem exatamente dois participantes e não aceita novos membros.");

        RequireAtLeast(actorId, MemberRole.Admin, "adicionar membros");

        var existing = FindMember(userId);

        if (existing is not null)
        {
            DomainException.Require(!existing.IsActive, "Este usuário já é membro do grupo.");
            existing.Rejoin(now);
            existing.ChangeRole(role);
        }
        else
        {
            _members.Add(ConversationMember.Create(Id, userId, role, now));
        }

        return Message.CreateSystem(Id, actorId, SystemEventKind.MemberJoined, now, userId);
    }

    /// <summary>
    /// Tira alguém do grupo. O próprio membro pode sair; tirar outra pessoa exige admin.
    /// </summary>
    public Message RemoveMember(Guid actorId, Guid userId, DateTimeOffset now)
    {
        DomainException.Require(
            Type == ConversationType.Group,
            "Não é possível sair de uma conversa direta.");

        var alvo = RequireActiveMember(userId);
        var ehSaidaPropria = actorId == userId;

        if (!ehSaidaPropria)
        {
            RequireAtLeast(actorId, MemberRole.Admin, "remover membros");

            // Um admin não derruba outro admin nem o dono: sem isso, dois administradores
            // poderiam se expulsar mutuamente numa corrida, e o grupo ficaria à mercê de
            // quem clicar primeiro.
            var autor = RequireActiveMember(actorId);

            DomainException.Require(
                autor.Role > alvo.Role,
                "Você só pode remover membros de cargo inferior ao seu.");
        }

        DomainException.Require(
            alvo.Role != MemberRole.Owner,
            "O dono precisa transferir o grupo antes de sair.");

        alvo.Leave(now);

        return Message.CreateSystem(
            Id, actorId,
            ehSaidaPropria ? SystemEventKind.MemberLeft : SystemEventKind.MemberRemoved,
            now, userId);
    }

    /// <summary>Promove ou rebaixa um membro. Só o dono decide cargos.</summary>
    public Message ChangeRole(Guid actorId, Guid userId, MemberRole role, DateTimeOffset now)
    {
        DomainException.Require(Type == ConversationType.Group, "Conversa direta não tem cargos.");
        DomainException.Require(role != MemberRole.Owner, "Use TransferOwnership para trocar o dono.");

        RequireAtLeast(actorId, MemberRole.Owner, "alterar cargos");

        var member = RequireActiveMember(userId);

        DomainException.Require(member.Role != MemberRole.Owner, "O cargo do dono não pode ser rebaixado.");

        member.ChangeRole(role);

        return Message.CreateSystem(Id, actorId, SystemEventKind.RoleChanged, now, userId, role.ToString());
    }

    public Message TransferOwnership(Guid fromUserId, Guid toUserId, DateTimeOffset now)
    {
        DomainException.Require(Type == ConversationType.Group, "Conversa direta não tem dono.");

        var current = RequireActiveMember(fromUserId);
        var next = RequireActiveMember(toUserId);

        DomainException.Require(current.Role == MemberRole.Owner, "Só o dono pode transferir o grupo.");
        DomainException.Require(fromUserId != toUserId, "O grupo já é seu.");

        current.ChangeRole(MemberRole.Admin);
        next.ChangeRole(MemberRole.Owner);

        return Message.CreateSystem(Id, fromUserId, SystemEventKind.OwnershipTransferred, now, toUserId);
    }

    public Message Rename(Guid actorId, string title, DateTimeOffset now)
    {
        DomainException.Require(Type == ConversationType.Group, "Conversa direta não tem título.");

        RequireAtLeast(actorId, MemberRole.Admin, "renomear o grupo");
        ValidateTitle(title);

        Title = title.Trim();

        return Message.CreateSystem(Id, actorId, SystemEventKind.TitleChanged, now, body: Title);
    }

    public void SetAvatar(Guid actorId, string? avatarUrl)
    {
        DomainException.Require(Type == ConversationType.Group, "Conversa direta não tem avatar próprio.");
        RequireAtLeast(actorId, MemberRole.Admin, "trocar a imagem do grupo");

        AvatarUrl = avatarUrl;
    }

    /// <summary>Silencia (ou desativa o silêncio) para um membro. Só afeta quem chamou.</summary>
    public void Mute(Guid userId, DateTimeOffset? until)
    {
        RequireActiveMember(userId).MuteUntil(until);
    }

    public bool IsActiveMember(Guid userId) => FindMember(userId)?.IsActive ?? false;

    public ConversationMember? FindMember(Guid userId) =>
        _members.Find(m => m.UserId == userId);

    /// <summary>
    /// Cria uma mensagem já validando que o remetente é membro ativo, e adianta o
    /// <see cref="LastMessageAt"/>. Passar por aqui é o que impede o resto do código de
    /// gravar mensagem em conversa da qual a pessoa não participa.
    /// </summary>
    public Message PostMessage(Guid senderId, string body, Guid clientMessageId, DateTimeOffset now,
        Guid? replyToMessageId = null)
    {
        DomainException.Require(
            IsActiveMember(senderId),
            "Só membros ativos da conversa podem enviar mensagens.");

        var message = Message.Create(Id, senderId, body, clientMessageId, now, replyToMessageId);

        if (now > LastMessageAt)
        {
            LastMessageAt = now;
        }

        return message;
    }

    private ConversationMember RequireActiveMember(Guid userId)
    {
        var member = FindMember(userId);
        DomainException.Require(member?.IsActive == true, "Usuário não é membro ativo desta conversa.");
        return member!;
    }

    /// <summary>
    /// Exige que o autor da ação tenha pelo menos o cargo informado.
    /// </summary>
    /// <remarks>
    /// Os valores de <see cref="MemberRole"/> são crescentes em poder, então a comparação é
    /// um simples <c>&gt;=</c>. Concentrar a checagem aqui é o que impede a permissão de
    /// virar um <c>if</c> repetido em cada endpoint — e um deles, um dia, esquecido.
    /// </remarks>
    private void RequireAtLeast(Guid actorId, MemberRole minimo, string acao)
    {
        var autor = RequireActiveMember(actorId);

        DomainException.Require(
            autor.Role >= minimo,
            $"É preciso ser {(minimo == MemberRole.Owner ? "dono" : "administrador")} para {acao}.");
    }

    private static void ValidateTitle(string title)
    {
        DomainException.Require(
            !string.IsNullOrWhiteSpace(title),
            "O título do grupo não pode ser vazio.");

        DomainException.Require(
            title.Trim().Length <= MaxTitleLength,
            $"O título do grupo não pode passar de {MaxTitleLength} caracteres.");
    }
}
