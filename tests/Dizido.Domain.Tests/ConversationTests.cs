using Dizido.Domain;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;

namespace Dizido.Domain.Tests;

public sealed class ConversationTests
{
    // Instante fixo: nenhum teste depende do relógio da máquina, então nunca falha
    // "às vezes" nem se comporta diferente na CI.
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bruno = Guid.CreateVersion7();
    private static readonly Guid Carla = Guid.CreateVersion7();

    [Fact]
    public void PrivadoNasceComOsDoisParticipantes()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);

        Assert.Equal(ConversationType.Direct, conversa.Type);
        Assert.Equal(2, conversa.ActiveMembers.Count());
        Assert.True(conversa.IsActiveMember(Alice));
        Assert.True(conversa.IsActiveMember(Bruno));
        Assert.Null(conversa.Title);
    }

    [Fact]
    public void PrivadoConsigoMesmoEhRejeitado()
    {
        var erro = Assert.Throws<DomainException>(() => Conversation.CreateDirect(Alice, Alice, Now));

        Assert.Contains("consigo mesmo", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivadoNaoAceitaTerceiroParticipante()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);

        Assert.Throws<DomainException>(() => conversa.AddMember(Alice, Carla, Now));
    }

    [Fact]
    public void GrupoExigeTitulo()
    {
        Assert.Throws<DomainException>(() => Conversation.CreateGroup("   ", Alice, Now));
    }

    [Fact]
    public void QuemCriaOGrupoEhODono()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);

        Assert.Equal(MemberRole.Owner, grupo.FindMember(Alice)!.Role);
    }

    [Fact]
    public void DonoNaoPodeSairSemTransferir()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);

        Assert.Throws<DomainException>(() => grupo.RemoveMember(Alice, Alice, Now));

        grupo.TransferOwnership(Alice, Bruno, Now);
        grupo.RemoveMember(Alice, Alice, Now); // agora pode

        Assert.False(grupo.IsActiveMember(Alice));
        Assert.Equal(MemberRole.Owner, grupo.FindMember(Bruno)!.Role);
    }

    [Fact]
    public void QuemSaiuContinuaRegistrado()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);
        grupo.RemoveMember(Bruno, Bruno, Now);

        // A linha continua existindo: as mensagens antigas do Bruno ainda fazem sentido.
        Assert.Single(grupo.Members, m => m.UserId == Bruno);
        Assert.False(grupo.IsActiveMember(Bruno));
        Assert.NotNull(grupo.FindMember(Bruno)!.LeftAt);
    }

    [Fact]
    public void QuemVoltaReaproveitaAMesmaLinha()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);
        grupo.RemoveMember(Bruno, Bruno, Now);
        grupo.AddMember(Alice, Bruno, Now.AddHours(1));

        // Chave composta (ConversationId, UserId): não pode haver duas linhas do Bruno.
        Assert.Single(grupo.Members, m => m.UserId == Bruno);
        Assert.True(grupo.IsActiveMember(Bruno));
    }

    [Fact]
    public void NaoMembroNaoConsegueEnviarMensagem()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);

        Assert.Throws<DomainException>(
            () => grupo.PostMessage(Carla, "oi", Guid.NewGuid(), Now));
    }

    [Fact]
    public void ExMembroNaoConsegueEnviarMensagem()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);
        grupo.RemoveMember(Bruno, Bruno, Now);

        Assert.Throws<DomainException>(
            () => grupo.PostMessage(Bruno, "voltei", Guid.NewGuid(), Now));
    }

    [Fact]
    public void EnviarMensagemAdiantaAAtividadeDaConversa()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var depois = Now.AddMinutes(30);

        conversa.PostMessage(Alice, "e aí", Guid.NewGuid(), depois);

        Assert.Equal(depois, conversa.LastMessageAt);
    }

    [Fact]
    public void MarcaDeLeituraNuncaRetrocede()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var membro = conversa.FindMember(Bruno)!;

        var primeira = conversa.PostMessage(Alice, "um", Guid.NewGuid(), Now);
        var segunda = conversa.PostMessage(Alice, "dois", Guid.NewGuid(), Now.AddSeconds(1));

        membro.MarkReadUpTo(segunda.Id);
        membro.MarkReadUpTo(primeira.Id); // chegou fora de ordem — deve ser ignorado

        Assert.Equal(segunda.Id, membro.LastReadMessageId);
    }

    // ------------------------------------------------------------------
    // Permissões de grupo (Fase 6)
    // ------------------------------------------------------------------

    private static Conversation GrupoComTodos()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);
        grupo.AddMember(Alice, Carla, Now);
        return grupo;
    }

    [Fact]
    public void MembroComumNaoAdicionaNemRemoveNemRenomeia()
    {
        var grupo = GrupoComTodos();

        Assert.Throws<DomainException>(() => grupo.AddMember(Bruno, Guid.CreateVersion7(), Now));
        Assert.Throws<DomainException>(() => grupo.RemoveMember(Bruno, Carla, Now));
        Assert.Throws<DomainException>(() => grupo.Rename(Bruno, "outro nome", Now));
    }

    [Fact]
    public void MembroComumSempreConsegueSairSozinho()
    {
        var grupo = GrupoComTodos();

        var aviso = grupo.RemoveMember(Bruno, Bruno, Now);

        Assert.False(grupo.IsActiveMember(Bruno));
        Assert.Equal(SystemEventKind.MemberLeft, aviso.SystemEvent);
    }

    [Fact]
    public void AdminNaoRemoveOutroAdmin()
    {
        var grupo = GrupoComTodos();
        grupo.ChangeRole(Alice, Bruno, MemberRole.Admin, Now);
        grupo.ChangeRole(Alice, Carla, MemberRole.Admin, Now);

        // Sem esta regra, dois admins poderiam se expulsar mutuamente numa corrida.
        Assert.Throws<DomainException>(() => grupo.RemoveMember(Bruno, Carla, Now));
    }

    [Fact]
    public void AdminRemoveMembroComum()
    {
        var grupo = GrupoComTodos();
        grupo.ChangeRole(Alice, Bruno, MemberRole.Admin, Now);

        var aviso = grupo.RemoveMember(Bruno, Carla, Now);

        Assert.False(grupo.IsActiveMember(Carla));
        Assert.Equal(SystemEventKind.MemberRemoved, aviso.SystemEvent);
        Assert.Equal(Carla, aviso.SystemTargetId);
    }

    [Fact]
    public void AdminNaoRemoveODono()
    {
        var grupo = GrupoComTodos();
        grupo.ChangeRole(Alice, Bruno, MemberRole.Admin, Now);

        Assert.Throws<DomainException>(() => grupo.RemoveMember(Bruno, Alice, Now));
    }

    [Fact]
    public void SoODonoAlteraCargos()
    {
        var grupo = GrupoComTodos();
        grupo.ChangeRole(Alice, Bruno, MemberRole.Admin, Now);

        // Nem mesmo um admin promove alguém: só o dono.
        Assert.Throws<DomainException>(() => grupo.ChangeRole(Bruno, Carla, MemberRole.Admin, Now));
    }

    [Fact]
    public void RenomearGeraAvisoDeSistemaComONovoTitulo()
    {
        var grupo = GrupoComTodos();

        var aviso = grupo.Rename(Alice, "  Nuclear  ", Now);

        Assert.Equal("Nuclear", grupo.Title);
        Assert.Equal(SystemEventKind.TitleChanged, aviso.SystemEvent);
        Assert.Equal("Nuclear", aviso.Body);
        Assert.True(aviso.IsSystem);
    }

    [Fact]
    public void TransferirDonoTrocaOsCargosDosDois()
    {
        var grupo = GrupoComTodos();

        var aviso = grupo.TransferOwnership(Alice, Bruno, Now);

        Assert.Equal(MemberRole.Owner, grupo.FindMember(Bruno)!.Role);
        Assert.Equal(MemberRole.Admin, grupo.FindMember(Alice)!.Role);
        Assert.Equal(SystemEventKind.OwnershipTransferred, aviso.SystemEvent);
    }

    [Fact]
    public void SoODonoTransfereOGrupo()
    {
        var grupo = GrupoComTodos();

        Assert.Throws<DomainException>(() => grupo.TransferOwnership(Bruno, Carla, Now));
    }

    [Fact]
    public void SilenciarAfetaApenasQuemPediu()
    {
        var grupo = GrupoComTodos();
        var ate = Now.AddHours(8);

        grupo.Mute(Bruno, ate);

        Assert.Equal(ate, grupo.FindMember(Bruno)!.MutedUntil);
        Assert.Null(grupo.FindMember(Carla)!.MutedUntil);
    }
}
