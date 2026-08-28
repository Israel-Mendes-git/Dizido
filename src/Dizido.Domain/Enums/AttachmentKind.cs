namespace Dizido.Domain.Enums;

/// <summary>Como o anexo deve ser tratado pelo cliente.</summary>
/// <remarks>
/// A distinção não é cosmética, é de segurança. Uma imagem é exibida <b>dentro</b> da página,
/// e o navegador precisa interpretar os bytes para desenhá-la. Um arquivo qualquer nunca é
/// interpretado: vai para o disco do usuário com <c>Content-Disposition: attachment</c>.
/// <para>
/// Por isso só o que entra como <see cref="Image"/> tem os bytes conferidos contra uma lista
/// curta de formatos conhecidos. Deixar um HTML se passar por imagem e ser exibido na origem
/// do app seria XSS armazenado — o atacante sobe um "gif", o navegador de quem abre executa.
/// </para>
/// </remarks>
public enum AttachmentKind
{
    Image = 1,
    File = 2,
}

/// <summary>Em que ponto do upload em três passos o anexo está.</summary>
/// <remarks>
/// <para>
/// O arquivo não passa pela API: o cliente sobe direto para o object storage com uma URL
/// assinada. Isso deixa um intervalo em que a linha existe no banco mas os bytes ainda não
/// existem (ou nunca vão existir, se o upload falhar no meio) — é o que <see cref="Pending"/>
/// representa.
/// </para>
/// <para>
/// Nada consome um anexo <see cref="Pending"/>: ele não pode ser anexado a mensagem nem
/// baixado. Só depois de o servidor conferir os bytes que chegaram é que vira
/// <see cref="Ready"/>. Sem esses dois estados, um upload interrompido viraria uma mensagem
/// com imagem quebrada, para sempre.
/// </para>
/// </remarks>
public enum AttachmentStatus
{
    Pending = 1,
    Ready = 2,
}
