namespace Dizido.Domain.Enums;

/// <summary>Natureza da conversa. Define regras diferentes de membros e título.</summary>
public enum ConversationType
{
    /// <summary>Conversa privada entre exatamente duas pessoas. Sem título, sem dono.</summary>
    Direct = 1,

    /// <summary>Grupo com título, dono e número variável de membros.</summary>
    Group = 2,
}
