#!/bin/sh
# Restaura um backup do Dizido — e, mais importante, permite TESTAR se ele restaura.
#
#   sh deploy/restaurar.sh --listar
#   sh deploy/restaurar.sh --testar                  confere o backup mais recente num banco descartável
#   sh deploy/restaurar.sh --restaurar <arquivo>     restaura POR CIMA do banco de produção
#
# Backup que nunca foi restaurado não é backup: é um arquivo do qual você tem esperança. O
# modo --testar existe para essa esperança virar evidência — ele cria um banco temporário,
# restaura o dump lá, conta as linhas e joga fora. Rode de tempos em tempos.

set -eu

COMPOSE="docker compose -f docker-compose.prod.yml"
VOLUME_BACKUPS=$($COMPOSE config --format json 2>/dev/null | grep -o 'dizido_backups' | head -1 || echo "dizido_backups")

listar() {
	echo "Backups disponíveis:"
	$COMPOSE run --rm --no-deps -T backup sh -c 'ls -lh /backups/*.dump 2>/dev/null || echo "  (nenhum ainda)"'
}

testar() {
	echo "== teste de restauração =="
	echo

	ULTIMO=$($COMPOSE run --rm --no-deps -T backup sh -c 'ls -1t /backups/*.dump 2>/dev/null | head -1' | tr -d '\r')

	if [ -z "$ULTIMO" ]; then
		echo "Nenhum backup encontrado. O serviço de backup está rodando?"
		exit 1
	fi

	echo "arquivo: $ULTIMO"
	echo

	# Um banco descartável DENTRO do mesmo Postgres. Restaurar por cima do banco real para
	# "ver se funciona" é como testar o extintor ateando fogo na sala.
	TEMP="restauracao_teste_$(date -u +%s)"

	echo "criando banco temporário $TEMP..."
	$COMPOSE exec -T db psql -U dizido -d postgres -c "CREATE DATABASE \"$TEMP\";"

	echo "restaurando..."
	# --no-owner e --no-privileges: o dump traz o dono e as permissões do banco original, que
	# não precisam existir aqui. Sem eles, a restauração enche a tela de erros irrelevantes.
	if $COMPOSE run --rm --no-deps -T backup \
		pg_restore --host=db --username=dizido --dbname="$TEMP" \
		--no-owner --no-privileges "$ULTIMO"; then

		echo
		echo "conferindo o conteúdo:"
		$COMPOSE exec -T db psql -U dizido -d "$TEMP" -c \
			"SELECT 'perfis' AS tabela, count(*) FROM user_profiles
			 UNION ALL SELECT 'conversas', count(*) FROM conversations
			 UNION ALL SELECT 'mensagens', count(*) FROM messages
			 UNION ALL SELECT 'anexos', count(*) FROM attachments;"

		RESULTADO="OK"
	else
		RESULTADO="FALHOU"
	fi

	echo
	echo "descartando o banco temporário..."
	$COMPOSE exec -T db psql -U dizido -d postgres -c "DROP DATABASE \"$TEMP\";"

	echo
	echo "== teste de restauração: $RESULTADO =="

	[ "$RESULTADO" = "OK" ] || exit 1
}

restaurar() {
	ARQUIVO=$1

	echo "ATENÇÃO: isto substitui o conteúdo do banco de PRODUÇÃO por $ARQUIVO."
	printf "Digite 'restaurar' para confirmar: "
	read -r CONFIRMACAO

	[ "$CONFIRMACAO" = "restaurar" ] || { echo "cancelado."; exit 1; }

	# A aplicação para antes: restaurar com a API escrevendo produz um resultado que não é
	# nem o backup nem o estado atual.
	echo "parando a aplicação..."
	$COMPOSE stop api caddy

	echo "restaurando..."
	$COMPOSE run --rm --no-deps -T backup \
		pg_restore --host=db --username=dizido --dbname=dizido \
		--clean --if-exists --no-owner --no-privileges "$ARQUIVO"

	echo "subindo a aplicação..."
	$COMPOSE start api caddy

	echo "pronto."
}

case "${1:-}" in
	--listar) listar ;;
	--testar) testar ;;
	--restaurar) restaurar "${2:?informe o arquivo}" ;;
	*)
		echo "uso: $0 --listar | --testar | --restaurar <arquivo>"
		exit 1
		;;
esac
