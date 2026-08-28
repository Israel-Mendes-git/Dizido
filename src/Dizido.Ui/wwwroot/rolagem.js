// Rolagem da lista de mensagens.
//
// A regra que todo app de chat segue e que é irritante quando quebrada:
// se o usuário está no fim da lista, mensagem nova rola junto; se ele subiu para ler o
// histórico, NÃO rola — senão o app arranca a leitura dele do lugar a cada mensagem
// que chega no grupo.

const MARGEM = 120; // pixels de tolerância para considerar "está no fim"

export function rolarSeNoFim(id) {
    const elemento = document.getElementById(id);
    if (!elemento) return;

    const distanciaDoFim =
        elemento.scrollHeight - elemento.scrollTop - elemento.clientHeight;

    // A checagem acontece ANTES do navegador pintar o novo conteúdo, então
    // distanciaDoFim ainda reflete onde o usuário estava.
    if (distanciaDoFim <= MARGEM) {
        elemento.scrollTop = elemento.scrollHeight;
    }
}

export function rolarParaOFim(id) {
    const elemento = document.getElementById(id);
    if (elemento) elemento.scrollTop = elemento.scrollHeight;
}
