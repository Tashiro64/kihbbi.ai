from fastapi import FastAPI, UploadFile, File
from fastapi.responses import JSONResponse
from faster_whisper import WhisperModel
import tempfile
import os
import time

app = FastAPI()

# ------------------------------
# FAST CONFIG (RTX 3070 Ti)
# ------------------------------
MODEL_SIZE = "small"       # BEST speed/accuracy on GPU. Try "medium" later if wanted.
DEVICE = "cuda"
COMPUTE = "float16"        # fastest on NVIDIA GPU

model = WhisperModel(
    MODEL_SIZE,
    device=DEVICE,
    compute_type=COMPUTE,
)

@app.post("/stt")
async def stt(audio: UploadFile = File(...)):
    t0 = time.time()

    # Save upload to temp wav
    with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as tmp:
        tmp.write(await audio.read())
        path = tmp.name

    try:
        segments, info = model.transcribe(
            path,

            # IMPORTANT SPEED SETTINGS:
            beam_size=1,           # fastest decoding
            best_of=1,             # dont do extra decoding passes
            vad_filter=True,       # ignore silence
            temperature=0.0,       # deterministic, faster/stable
            condition_on_previous_text=False,  # prevents slow “context chaining”
            without_timestamps=True # slightly faster
        )

        text = " ".join(seg.text.strip() for seg in segments).strip()
        dt = time.time() - t0

        return JSONResponse({
            "text": text,
            "lang": info.language,
            "time_sec": round(dt, 3),
            "model": MODEL_SIZE,
            "device": DEVICE
        })
    finally:
        if os.path.exists(path):
            os.remove(path)