#!/bin/sh
# Backup do Postgres do Dizido.
#
# Roda em laço dentro de um contêiner, faz um dump por dia e apaga os antigos. Um cron de
# verdade seria mais elegante; um laço com `sleep` é imune a fuso horário, a variável de
# ambiente não herdada pelo cron e a "por que não rodou hoje?" — os três motivos pelos quais
# backup agendado costuma falhar em silêncio.
#
# ATENÇÃO: isto guarda os backups num volume da MESMA máquina. Serve contra "apaguei a tabela
# errada", que é o acidente mais comum, mas NÃO serve contra perder a máquina. Copie os
# arquivos para fora (outro servidor, um bucket, um disco) — backup que mora junto do original
# não é backup, é uma segunda cópia esperando o mesmo acidente.

set -eu

DESTINO=/backups
INTERVALO=${INTERVALO_SEGUNDOS:-86400}
RETENCAO=${RETENCAO_DIAS:-14}

mkdir -p "$DESTINO"

echo "backup: destino=$DESTINO retenção=${RETENCAO}d intervalo=${INTERVALO}s"

while true; do
	ARQUIVO="$DESTINO/dizido-$(date -u +%Y%m%d-%H%M%S).dump"

	echo "backup: gerando $ARQUIVO"

	# --format=custom, e não SQL puro: o formato próprio do Postgres é comprimido e permite
	# restaurar tabelas específicas com pg_restore, em vez de tudo ou nada.
	#
	# Escreve num arquivo temporário e só depois renomeia. Sem isso, um dump interrompido no
	# meio deixaria um arquivo com nome de backup válido e conteúdo pela metade — e ninguém
	# descobriria antes de precisar dele.
	if pg_dump --host=db --username=dizido --dbname=dizido \
		--format=custom --compress=9 --file="$ARQUIVO.parcial"; then

		mv "$ARQUIVO.parcial" "$ARQUIVO"
		echo "backup: pronto ($(du -h "$ARQUIVO" | cut -f1))"

		# A limpeza só acontece depois de um backup bem-sucedido. Se o dump está falhando há
		# uma semana, apagar os antigos por idade deixaria você sem nenhum.
		find "$DESTINO" -name 'dizido-*.dump' -type f -mtime "+$RETENCAO" -print -delete
	else
		echo "backup: FALHOU"
		rm -f "$ARQUIVO.parcial"
	fi

	sleep "$INTERVALO"
done
