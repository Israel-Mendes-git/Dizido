namespace Dizido.Domain.Enums;

/// <summary>Natureza de uma mensagem no fluxo da conversa.</summary>
public enum MessageKind
{
    /// <summary>Escrita por uma pessoa.</summary>
    Text = 1,

    /// <summary>
    /// Gerada pelo sistema ("Fulano entrou no grupo", "o título mudou").
    /// </summary>
    /// <remarks>
    /// Vive na mesma tabela das mensagens comuns, e não numa tabela de auditoria à parte,
    /// porque o que importa é a <b>posição cronológica</b>: "Fulano saiu" precisa aparecer
    /// exatamente entre a última mensagem dele e a próxima de outra pessoa. Com duas tabelas,
    /// intercalar isso na paginação seria trabalhoso e frágil.
    /// </remarks>
    System = 2,
}

/// <summary>O que a mensagem de sistema está anunciando.</summary>
/// <remarks>
/// Gravamos o código, não a frase pronta. Assim a tradução acontece na interface do leitor —
/// uma mensagem gravada em português não ficaria presa a esse idioma quando o app for
/// traduzido, e o texto pode ser reescrito sem migrar dados antigos.
/// </remarks>
public enum SystemEventKind
{
    MemberJoined = 1,
    MemberLeft = 2,
    MemberRemoved = 3,
    TitleChanged = 4,
    OwnershipTransferred = 5,
    RoleChanged = 6,
}
