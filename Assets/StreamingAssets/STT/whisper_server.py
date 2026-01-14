from fastapi import FastAPI, UploadFile, File
from fastapi.responses import JSONResponse
from faster_whisper import WhisperModel
import tempfile
import os

app = FastAPI()

# Good quality bilingual EN/FR:
model = WhisperModel("small", device="cpu", compute_type="int8")
# If NVIDIA GPU later:
# model = WhisperModel("medium", device="cuda", compute_type="float16")

@app.post("/stt")
async def stt(audio: UploadFile = File(...)):
    with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as tmp:
        tmp.write(await audio.read())
        path = tmp.name

    try:
        segments, info = model.transcribe(path)
        text = " ".join([seg.text.strip() for seg in segments]).strip()
        return JSONResponse({"text": text, "lang": info.language})
    finally:
        if os.path.exists(path):
            os.remove(path)