#!/usr/bin/env bash
# Runs the API against a chosen Ollama host.
#
#   ./run-local.sh                                  # local Ollama
#   ./run-local.sh https://xxx.trycloudflare.com    # Colab tunnel
#
# Qdrant and MinIO come from docker-compose; MinIO credentials come from .env,
# never from appsettings.
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
api_dir="$repo_dir/AiAssistant.Api"
pid_file="$repo_dir/.run/api.pid"
log_file="$repo_dir/.run/api.log"

ollama_host="${1:-http://localhost:11434}"

# Vector width must match the embedding model, or Qdrant rejects the upsert.
case "${EMBED_MODEL:-qwen3-embedding:8b}" in
  nomic-embed-text*) vector_size=768 ;;
  *)                 vector_size=4096 ;;
esac

mkdir -p "$repo_dir/.run"

if [[ -f "$pid_file" ]] && kill -0 "$(cat "$pid_file")" 2>/dev/null; then
  kill "$(cat "$pid_file")"
  sleep 2
fi

# shellcheck disable=SC1091
set -a; source "$repo_dir/.env"; set +a

dotnet build "$api_dir" --nologo --verbosity quiet

Storage__AccessKey="$MINIO_ROOT_USER" \
Storage__SecretKey="$MINIO_ROOT_PASSWORD" \
Ollama__Host="$ollama_host" \
Ollama__ChatModel="${CHAT_MODEL:-qwen3:8b}" \
Ollama__EmbedModel="${EMBED_MODEL:-qwen3-embedding:8b}" \
Qdrant__VectorSize="$vector_size" \
ASPNETCORE_URLS="http://localhost:5299" \
  setsid nohup dotnet "$api_dir/bin/Debug/net8.0/AiAssistant.Api.dll" \
  > "$log_file" 2>&1 < /dev/null &

echo $! > "$pid_file"
sleep 5

if curl -sf http://localhost:5299/health > /dev/null; then
  echo "API tayyor: http://localhost:5299  (Ollama: $ollama_host, vector: $vector_size)"
else
  echo "API ko'tarilmadi. Log:"
  tail -20 "$log_file"
  exit 1
fi
