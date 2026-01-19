import io
import os
import re
import torch
import soundfile as sf
from fastapi import FastAPI
from fastapi.responses import Response, JSONResponse
from pydantic import BaseModel
from TTS.api import TTS

app = FastAPI()

print("Loading XTTS v2...")
# Use CUDA with proper memory management
USE_CUDA = True
device = "cuda" if (USE_CUDA and torch.cuda.is_available()) else "cpu"
print(f"Using device: {device}")

# Check CUDA availability and memory
if torch.cuda.is_available() and USE_CUDA:
    print(f"CUDA detected: {torch.cuda.get_device_name(0)}")
    print(f"CUDA memory: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f}GB")
    
    # Clear CUDA cache and set memory fraction
    torch.cuda.empty_cache()
    torch.cuda.set_per_process_memory_fraction(0.8)  # Use 80% of GPU memory
    print("CUDA optimized for XTTS")
else:
    print("Using CPU mode")

tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to(device)

# Track generation count for periodic cleanup
generation_count = 0

# Supported languages for XTTS-v2
SUPPORTED_LANGUAGES = ["en", "es", "fr", "de", "it", "pt", "pl", "tr", "ru", "nl", "cs", "ar", "zh-cn", "ja", "hu", "ko", "hi"]

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# Put your default wav here:
# E:\Unity Projects\Kihbbi.AI\Assets\StreamingAssets\TTS\speaker.wav
DEFAULT_SPEAKER_WAV = os.path.join(BASE_DIR, "speaker.wav")


class TTSRequest(BaseModel):
    text: str
    language: str = "en"
    speaker_wav: str | None = None


def sanitize_text(text: str) -> str:
    """
    Aggressive text sanitization to prevent CUDA indexing errors.
    The indexing errors are often caused by unusual character combinations.
    """
    if not text or not text.strip():
        return ""
    
    # First pass: Remove problematic characters that cause CUDA errors
    text = text.strip()
    
    # Remove smart quotes and replace with regular quotes
    text = text.replace('"', '"').replace('"', '"').replace("'", "'").replace("'", "'")
    
    # Remove em dashes, en dashes and replace with regular hyphens
    text = text.replace('—', '-').replace('–', '-')
    
    # Remove ellipsis and replace with three dots
    text = text.replace('…', '...')
    
    # Convert to ASCII and remove non-printable characters
    text = text.encode('ascii', 'ignore').decode('ascii')
    text = ''.join(c for c in text if c.isprintable() or c in ' \n\r\t')
    
    # Keep only safe characters (letters, numbers, basic punctuation)
    # This is more restrictive to prevent CUDA indexing errors
    text = re.sub(r'[^a-zA-Z0-9\s.,!?\-\'\" ]', ' ', text)
    
    # Clean up multiple spaces and punctuation
    text = re.sub(r'\s+', ' ', text)  # Multiple spaces -> single space
    text = re.sub(r'[.,!?]{2,}', '.', text)  # Multiple punctuation -> single
    text = re.sub(r'\s+[.,!?]\s+', '. ', text)  # Space around punctuation
    
    # Remove leading/trailing punctuation and whitespace
    text = text.strip(' .,!?;:-')
    
    # Ensure we have meaningful content
    words = [word.strip('.,!?') for word in text.split() if any(c.isalnum() for c in word)]
    if len(words) < 2:
        return ""
    
    # Reconstruct with safe spacing
    text = ' '.join(words)
    
    # Final length check
    if len(text) < 5 or len(text) > 200:  # Keep reasonable bounds
        return ""
    
    return text


def resolve_speaker_path(speaker_wav: str | None) -> str | None:
    """
    Resolves speaker wav path:
    - if speaker_wav is provided:
        - if absolute and exists => use it
        - if relative => resolve relative to BASE_DIR
    - else => use DEFAULT_SPEAKER_WAV if exists
    """
    # If request provided a speaker_wav
    if speaker_wav and speaker_wav.strip():
        sp = speaker_wav.strip()

        # If relative path, resolve relative to this script folder
        if not os.path.isabs(sp):
            sp = os.path.join(BASE_DIR, sp)

        if os.path.isfile(sp):
            return sp
        else:
            print(f"[XTTS] speaker_wav provided but file not found: {sp}")

    # Fallback
    if os.path.isfile(DEFAULT_SPEAKER_WAV):
        return DEFAULT_SPEAKER_WAV

    # No valid speaker
    return None


@app.post("/tts")
def tts_endpoint(req: TTSRequest):
    text = (req.text or "").strip()
    print(f"[XTTS] === NEW REQUEST ===")
    print(f"[XTTS] Raw input text: '{text}' (length: {len(text)})")
    
    if not text:
        print("[XTTS] ERROR: Empty text received")
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")

    # Sanitize text to prevent CUDA errors
    original_text = text
    text = sanitize_text(text)
    
    print(f"[XTTS] Original: '{original_text}'")
    print(f"[XTTS] Sanitized: '{text}' (length: {len(text)})")
    
    # Reject if empty or too short after sanitization
    if not text or len(text.strip()) < 5:
        print(f"[XTTS] ERROR: Text too short or empty after sanitization. Original: '{original_text[:100]}'")
        print(f"[XTTS] Sanitized result: '{text}'")
        # Return empty wav instead of error to avoid breaking the flow
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")
    
    # Additional validation: ensure we have meaningful content
    words = [word for word in text.split() if any(c.isalnum() for c in word)]
    if len(words) < 2:
        print(f"[XTTS] ERROR: Not enough meaningful words. Text: '{text}' -> {len(words)} words")
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")

    # Validate language
    lang = req.language.lower()
    if lang not in SUPPORTED_LANGUAGES:
        print(f"[XTTS] WARNING: Unsupported language '{lang}', defaulting to 'en'")
        lang = "en"

    # Limit text length to prevent memory issues
    MAX_LENGTH = 200  # Reduced from 250 for better stability
    if len(text) > MAX_LENGTH:
        print(f"[XTTS] WARNING: Text too long ({len(text)} chars), truncating to {MAX_LENGTH}")
        text = text[:MAX_LENGTH].rsplit(' ', 1)[0]  # Cut at word boundary
        # Ensure we still have meaningful content after truncation
        if len(text.strip()) < 10:
            print("[XTTS] ERROR: Text too short after truncation")
            buf = io.BytesIO()
            sf.write(buf, [0.0] * 2400, 24000, format="WAV")
            return Response(content=buf.getvalue(), media_type="audio/wav")

    speaker = resolve_speaker_path(req.speaker_wav)

    print(f"[XTTS] text_len={len(text)} lang={lang} speaker={speaker}")
    print(f"[XTTS] text_preview='{text[:100]}...'")
    print(f"[XTTS] Speaker file exists: {speaker and os.path.isfile(speaker) if speaker else False}")

    # IMPORTANT:
    # XTTS multi-speaker models require a speaker.
    # If we have none, we return silence instead of crashing.
    if not speaker:
        print("[XTTS] ERROR: No valid speaker wav found. Returning empty wav.")
        # Return empty wav
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")  # 0.1 sec silence
        return Response(content=buf.getvalue(), media_type="audio/wav")
    
    if not os.path.isfile(speaker):
        print(f"[XTTS] ERROR: Speaker file does not exist: {speaker}")
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")

    # Final validation before TTS generation
    try:
        # Ensure text is not just whitespace
        if not text.strip():
            raise ValueError("Text is empty or whitespace only")
            
        # Ensure we have alphanumeric characters
        alphanumeric_count = sum(c.isalnum() for c in text)
        if alphanumeric_count < 5:
            raise ValueError(f"Not enough alphanumeric characters ({alphanumeric_count})")
            
        # Ensure we have actual words
        word_count = len([w for w in text.split() if any(c.isalnum() for c in w)])
        if word_count < 2:
            raise ValueError(f"Not enough words ({word_count})")
            
        print(f"[XTTS] Final validation passed: {alphanumeric_count} chars, {word_count} words")
        
    except ValueError as ve:
        print(f"[XTTS] Pre-generation validation failed: {ve}")
        print(f"[XTTS] Problematic text: '{text}'")
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")

    # Generate with proper CUDA memory management
    try:
        global generation_count
        generation_count += 1
        
        print(f"[XTTS] Starting TTS generation #{generation_count} for: '{text[:50]}...'")
        
        # CUDA memory management
        if torch.cuda.is_available():
            # Clear cache every 5 generations to prevent memory buildup
            if generation_count % 5 == 0:
                torch.cuda.empty_cache()
                print(f"[XTTS] CUDA cache cleared (generation #{generation_count})")
        
        # Enhanced text preprocessing to prevent indexing errors
        # Ensure text doesn't start or end with punctuation
        text = text.strip('.,!?;: ')
        if not text:
            raise ValueError("Empty text after punctuation cleanup")
        
        # Add a period at the end if there's no punctuation
        if not text[-1] in '.!?':
            text = text + '.'
            
        print(f"[XTTS] Final processed text: '{text}'")
        
        # TTS generation with tensor cleanup
        with torch.no_grad():  # Prevent gradient computation to save memory
            try:
                wav = tts.tts(
                    text=text,
                    speaker_wav=speaker,
                    language=lang
                )
            except RuntimeError as e:
                error_str = str(e).lower()
                if "index" in error_str or "assert" in error_str or "cuda" in error_str:
                    print(f"[XTTS] CUDA indexing error: {e}")
                    print(f"[XTTS] Attempting recovery with simplified text...")
                    
                    # Simplify text further and retry
                    simple_text = re.sub(r'[^a-zA-Z0-9\s.]', '', text)
                    simple_text = ' '.join(simple_text.split())
                    if len(simple_text) < 3:
                        simple_text = "Hello world."  # Fallback to known working text
                    
                    print(f"[XTTS] Retry with: '{simple_text}'")
                    
                    # Clear CUDA cache before retry
                    if torch.cuda.is_available():
                        torch.cuda.empty_cache()
                    
                    wav = tts.tts(
                        text=simple_text,
                        speaker_wav=speaker,
                        language=lang
                    )
                    print("[XTTS] Recovery successful")
                else:
                    raise e
            
            except IndexError as idx_err:
                print(f"[XTTS] IndexError in TTS model: {idx_err}")
                print(f"[XTTS] This usually means the text is problematic for the model")
                print(f"[XTTS] Text: '{text}'")
                print(f"[XTTS] Length: {len(text)}")
                print(f"[XTTS] Language: {lang}")
                # Return silence instead of propagating the error
                buf = io.BytesIO()
                sf.write(buf, [0.0] * 4800, 24000, format="WAV")  # 0.2 sec silence
                return Response(content=buf.getvalue(), media_type="audio/wav")
            
            except Exception as model_err:
                print(f"[XTTS] Model error: {type(model_err).__name__}: {model_err}")
                print(f"[XTTS] Text that caused error: '{text}'")
                buf = io.BytesIO()
                sf.write(buf, [0.0] * 4800, 24000, format="WAV")  # 0.2 sec silence
                return Response(content=buf.getvalue(), media_type="audio/wav")

        if wav is None or len(wav) == 0:
            print("[XTTS] ERROR: TTS returned empty audio")
            buf = io.BytesIO()
            sf.write(buf, [0.0] * 2400, 24000, format="WAV")
            return Response(content=buf.getvalue(), media_type="audio/wav")

        # Check for silent audio (all values near zero)
        max_amplitude = max(abs(sample) for sample in wav) if len(wav) > 0 else 0
        if max_amplitude < 0.001:  # Very quiet audio
            print(f"[XTTS] WARNING: Generated audio is very quiet (max amplitude: {max_amplitude})")
            print(f"[XTTS] Audio stats: min={min(wav):.6f}, max={max(wav):.6f}, mean={sum(wav)/len(wav):.6f}")

        buf = io.BytesIO()
        sf.write(buf, wav, 24000, format="WAV")
        audio_size = len(buf.getvalue())
        print(f"[XTTS] SUCCESS: Generated {len(wav)} samples -> {audio_size} bytes WAV, max_amplitude={max_amplitude:.6f}")
        return Response(content=buf.getvalue(), media_type="audio/wav")
    
    except IndexError as e:
        print(f"[XTTS] IndexError during TTS generation: {e}")
        print(f"[XTTS] Text that caused error: '{text}'")
        print(f"[XTTS] Text length: {len(text)}")
        print(f"[XTTS] Language: {lang}")
        print(f"[XTTS] Speaker: {speaker}")
        # Return silent WAV
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")
    
    except RuntimeError as e:
        error_msg = str(e)
        print(f"[XTTS] RuntimeError during TTS generation: {error_msg}")
        print(f"[XTTS] Text: {text[:200]}")
        print(f"[XTTS] Language: {lang}")
        print(f"[XTTS] Speaker: {speaker}")
        
        # Return silent WAV instead of 500 error
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")
    
    except Exception as e:
        print(f"[XTTS] Unexpected error: {type(e).__name__}: {e}")
        print(f"[XTTS] Text that caused error: '{text}'")
        # Return silent WAV instead of crashing
        buf = io.BytesIO()
        sf.write(buf, [0.0] * 2400, 24000, format="WAV")
        return Response(content=buf.getvalue(), media_type="audio/wav")
