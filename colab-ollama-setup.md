# Colab'da Ollama serverini ko'tarish (sinov uchun)

Maqsad: GPU'li muhitda `qwen3:8b` + `qwen3-embedding:8b` modellarini ishga tushirib,
javob sifati va tezligini o'lchash. Production emas — Colab ToS bo'yicha doimiy
server sifatida ishlatib bo'lmaydi (sessiya uziladi).

## 1. Runtime

Runtime → Change runtime type → **T4 GPU** → Save

## 2. Ollama o'rnatish

```python
!curl -fsSL https://ollama.com/install.sh | sh
```

## 3. Serverni fonda ishga tushirish

```python
import subprocess, time, os
os.environ["OLLAMA_HOST"] = "0.0.0.0:11434"
subprocess.Popen(["ollama", "serve"])
time.sleep(5)
!ollama --version
```

## 4. Modellarni tortish

Ikkalasi ~10GB, tortish bir necha daqiqa oladi.

```python
!ollama pull qwen3:8b
!ollama pull qwen3-embedding:8b
!ollama list
```

## 5. Tezlik o'lchash (GPU vs CPU solishtirish uchun)

```python
!curl -s http://localhost:11434/api/generate -d '{"model":"qwen3:8b","prompt":"Toshkent qayerda joylashgan? Bir jumlada javob ber.","stream":false}' | python3 -c "import json,sys; d=json.load(sys.stdin); print('javob:', d['response']); print('load_duration s:', d['load_duration']/1e9); print('eval tok/s:', d['eval_count']/(d['eval_duration']/1e9))"
```

geodev serveridagi CPU natijasi solishtirish uchun: `load_duration` ~304s,
generatsiya ~4.5 tok/s (issiq holatda 0.75 tok/s gacha tushgan).

## 6. Tashqi tunnel ochish

```python
!wget -q https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64
!chmod +x cloudflared-linux-amd64
!./cloudflared-linux-amd64 tunnel --url http://localhost:11434
```

Chiqishda `https://<random>.trycloudflare.com` ko'rinadi — shu URL kerak.

**Diqqat:** bu URL parolsiz va ochiq. Faqat sinov davomida ishlating,
tugagach katakchani to'xtating.

## 7. AI Assistant API'ni shu tunnelga ulash

```bash
cd /home/user01/project/tech-support/ai-assistant/AiAssistant.Api
Ollama__Host=https://<random>.trycloudflare.com \
Qdrant__VectorSize=4096 \
ASPNETCORE_URLS=http://localhost:5299 \
dotnet run
```

`VectorSize=4096` — `qwen3-embedding:8b` vektor uzunligi.
Boshqa embed model ishlatilsa, o'lchamini moslash kerak
(`nomic-embed-text` = 768).

## 8. Tekshirish

```bash
curl -s http://localhost:5299/health

curl -s -X POST http://localhost:5299/api/documents/ingest \
  -F "file=@hujjat.txt"

curl -N -X POST http://localhost:5299/api/chat/stream \
  -H "Content-Type: application/json" \
  -d '{"message":"Litsenziya necha kunda beriladi?"}'
```
