import io
import os
import wave
import tempfile
from pathlib import Path
from fastapi import FastAPI
from fastapi.responses import Response
from pydantic import BaseModel

# Fix Windows encoding issues
import sys
import locale
if sys.platform == "win32":
    # Force UTF-8 encoding on Windows
    if hasattr(sys, 'set_int_max_str_digits'):
        try:
            # Set UTF-8 as default encoding
            import codecs
            sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'strict')
            sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'strict')
        except:
            pass
    
    # Set environment variable for UTF-8
    os.environ['PYTHONIOENCODING'] = 'utf-8'

from piper import PiperVoice
from piper.config import SynthesisConfig

app = FastAPI()

print("Loading Piper TTS...")
BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# Initialize Piper voice - using the simpler approach
try:
    # Try to load a downloaded model first, fallback to auto-download
    voice = None
    
    # Option 1: Look for downloaded model files in current directory
    onnx_files = list(Path(BASE_DIR).glob("*.onnx"))
    if onnx_files:
        model_path = str(onnx_files[0])  # Convert to string for better compatibility
        
        # Try loading with explicit encoding handling
        try:
            voice = PiperVoice.load(model_path)
        except UnicodeEncodeError as enc_error:
            # Try with different path handling
            model_path_short = os.path.basename(model_path)
            # Change to the directory and use relative path
            original_dir = os.getcwd()
            os.chdir(BASE_DIR)
            try:
                voice = PiperVoice.load(model_path_short)
            except Exception as e2:
                print(f"Model loading failed: {e2}")
            finally:
                os.chdir(original_dir)
    else:
        # Option 2: Download en_US-libritts_r-medium automatically
        print("No local model found, downloading en_US-libritts_r-medium...")
        import urllib.request
        import tarfile
        
        model_url = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/libritts_r/medium/en_US-libritts_r-medium.onnx"
        config_url = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/libritts_r/medium/en_US-libritts_r-medium.onnx.json"
        
        model_path = os.path.join(BASE_DIR, "en_US-libritts_r-medium.onnx")
        config_path = os.path.join(BASE_DIR, "en_US-libritts_r-medium.onnx.json")
        
        # Download model files
        urllib.request.urlretrieve(model_url, model_path)
        urllib.request.urlretrieve(config_url, config_path)
        
        # Change to the directory to avoid path encoding issues
        original_dir = os.getcwd()
        os.chdir(BASE_DIR)
        try:
            voice = PiperVoice.load("en_US-libritts_r-medium.onnx")  # Use relative path
        except Exception as load_error:
            print(f"Failed to load downloaded model: {load_error}")
        finally:
            os.chdir(original_dir)
    
except Exception as e:
    print(f"Failed to load Piper voice: {e}")
    # Create a dummy voice object for now
    voice = None


class TTSRequest(BaseModel):
    text: str
    language: str = "en"  # Not used by Piper but kept for API compatibility
    speaker_wav: str | None = None  # Not used by Piper but kept for compatibility
    speaker: str | None = None  # Used for speaker ID selection
    speaker_id: int | None = None  # Direct speaker ID (0-299 for libritts_r medium)
    length_scale: float | None = None  # Speech rate: 1.0=normal, >1.0=slower, <1.0=faster


@app.get("/health")
def health_check():
    """Health check endpoint"""
    if voice is None:
        return {"status": "error", "message": "Voice not loaded"}
    
    return {
        "status": "healthy",
        "voice_loaded": voice is not None,
        "model": "en_US-libritts_r-medium" if voice else None
    }


@app.get("/test")
def test_tts():
    """Test endpoint to verify Piper is working"""
    if voice is None:
        return {"error": "Voice not loaded"}
    
    test_text = "Hello world test"
    try:
        audio_buffer = io.BytesIO()
        voice.synthesize(test_text, audio_buffer)
        audio_buffer.seek(0)
        wav_data = audio_buffer.getvalue()
        
        return {
            "status": "success" if len(wav_data) > 44 else "failed",
            "audio_bytes": len(wav_data),
            "voice_info": str(voice),
            "test_text": test_text
        }
    except Exception as e:
        return {
            "status": "error",
            "error": str(e),
            "error_type": type(e).__name__
        }


@app.get("/speakers")
def get_speakers():
    """Get information about available speakers"""
    if voice is None:
        return {"error": "Voice not loaded"}
    
    # Note: The downloaded en_US-libritts_r-medium model is actually single-speaker
    # despite the name suggesting multiple speakers
    return {
        "model": "en_US-libritts_r-medium",
        "speaker_count": 1,
        "speaker_range": "Single speaker model",
        "description": "High-quality single speaker from LibriTTS dataset",
        "note": "This model has one consistent voice. For multiple speakers, you'd need a different model."
    }


@app.post("/tts")
def tts_endpoint(req: TTSRequest):
    if voice is None:
        print("[Piper] ERROR: Voice not loaded")
        return create_silent_wav(0.1)
        
    text = (req.text or "").strip()
    
    # Additional text validation
    if len(text) > 2000:  # Prevent extremely long text that might cause issues
        print(f"[Piper] WARNING: Text too long ({len(text)} chars), truncating")
        text = text[:2000]
    
    # Determine speaker ID
    speaker_id = None
    if req.speaker_id is not None:
        speaker_id = req.speaker_id
    elif req.speaker is not None:
        # Try to parse speaker as integer
        try:
            speaker_id = int(req.speaker)
        except ValueError:
            print(f"[Piper] WARNING: Could not parse speaker '{req.speaker}' as integer, using default")
    
    # Default to your preferred speaker if none specified
    if speaker_id is None:
        speaker_id = 0  # <-- CHANGE THIS NUMBER to your preferred speaker (0-903)
    
    # Clamp speaker ID to valid range (0-903 for libritts_r medium)
    speaker_id = max(0, min(903, speaker_id))
    
    # Handle length_scale (speech rate)
    length_scale = req.length_scale if req.length_scale is not None else 1.0
    length_scale = max(0.1, min(3.0, length_scale))  # Clamp to reasonable range
    
    print(f"[Piper] Generating TTS for: '{text[:100]}...' with speaker_id={speaker_id}, length_scale={length_scale}")
    
    # Create synthesis config
    try:
        syn_config = SynthesisConfig(
            speaker_id=speaker_id,
            length_scale=length_scale,
            normalize_audio=True,
            volume=1.0
        )
    except Exception as config_error:
        print(f"[Piper] ERROR creating SynthesisConfig: {config_error}")
        return create_silent_wav(0.1)
    
    if not text:
        print("[Piper] ERROR: Empty text received")
        return create_silent_wav(0.1)
    
    # Basic text cleaning
    text = text.strip()
    if len(text) < 2:
        print("[Piper] ERROR: Text too short")
        return create_silent_wav(0.1)
    
    try:
        # Try manual phoneme approach with speaker support (PRIORITY METHOD)
        try:
            
            # Get phonemes from text
            phonemes = voice.phonemize(text)
            
            # Convert phonemes to IDs - handle the list structure properly
            if hasattr(voice, 'phonemes_to_ids'):
                # Flatten the phonemes list if it's nested (phonemes returns list of lists)
                if phonemes and isinstance(phonemes[0], list):
                    flat_phonemes = []
                    for phoneme_list in phonemes:
                        flat_phonemes.extend(phoneme_list)
                    phonemes = flat_phonemes
                
                phoneme_ids = voice.phonemes_to_ids(phonemes)
                
                # Convert phonemes to audio WITH synthesis config
                try:
                    audio_data = voice.phoneme_ids_to_audio(phoneme_ids, syn_config=syn_config)
                except Exception as config_error:
                    try:
                        audio_data = voice.phoneme_ids_to_audio(phoneme_ids)
                    except Exception as basic_error:
                        audio_data = None
                
                # Check if audio_data is valid (handle numpy array properly)
                if audio_data is not None and (hasattr(audio_data, 'size') and audio_data.size > 0 or len(audio_data) > 0):
                    # Convert raw audio to WAV format
                    import wave as wave_module
                    import numpy as np
                    audio_buffer = io.BytesIO()
                    with wave_module.open(audio_buffer, 'wb') as wav_file:
                        wav_file.setnchannels(1)  # Mono
                        wav_file.setsampwidth(2)  # 16-bit
                        wav_file.setframerate(voice.config.sample_rate)
                        
                        # Ensure audio_data is bytes
                        if isinstance(audio_data, (list, tuple, np.ndarray)):
                            # Convert numpy array or list to bytes
                            if hasattr(audio_data, 'dtype'):
                                # It's already a numpy array - check its format
                                if audio_data.dtype == np.float32 or audio_data.dtype == np.float64:
                                    # Float audio data needs to be converted to int16
                                    # Clamp to [-1, 1] range first, then convert
                                    audio_clamped = np.clip(audio_data, -1.0, 1.0)
                                    audio_int16 = (audio_clamped * 32767).astype(np.int16)
                                    audio_bytes = audio_int16.tobytes()
                                elif audio_data.dtype == np.int16:
                                    # Already int16, use directly
                                    audio_bytes = audio_data.tobytes()
                                else:
                                    # Other format, convert to int16
                                    audio_float = audio_data.astype(np.float32)
                                    audio_clamped = np.clip(audio_float, -1.0, 1.0)
                                    audio_int16 = (audio_clamped * 32767).astype(np.int16)
                                    audio_bytes = audio_int16.tobytes()
                            else:
                                # Convert list/tuple to numpy array then bytes
                                audio_array = np.array(audio_data, dtype=np.float32)
                                # Clamp to [-1, 1] range first
                                audio_clamped = np.clip(audio_array, -1.0, 1.0)
                                audio_int16 = (audio_clamped * 32767).astype(np.int16)
                                audio_bytes = audio_int16.tobytes()
                        else:
                            audio_bytes = audio_data
                            
                        wav_file.writeframes(audio_bytes)
                    
                    wav_data = audio_buffer.getvalue()
                    
                    # Enhanced validation
                    if wav_data and len(wav_data) > 44:
                        print(f"[Piper] Manual method SUCCESS - Generated {len(wav_data)} bytes of audio")
                        return Response(content=wav_data, media_type="audio/wav")
                    else:
                        print(f"[Piper] Manual method generated invalid audio: {len(wav_data) if wav_data else 0} bytes")
                else:
                    print(f"[Piper] Manual method returned {len(audio_data) if audio_data else 0} audio samples")
            else:
                print(f"[Piper] Voice doesn't have phonemes_to_ids method")
            
        except Exception as method3_error:
            print(f"[Piper] Manual method failed: {method3_error}")
            print(f"[Piper] Error type: {type(method3_error)}")
            import traceback
            print(f"[Piper] Manual method traceback: {traceback.format_exc()}")
        
        # Method 1: Try synthesize_wav with proper WAV file object (no speaker support - FALLBACK)
        try:
            print(f"[Piper] Trying synthesize_wav fallback (no speaker selection)...")
            
            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp_file:
                temp_path = temp_file.name
            
            print(f"[Piper] Writing WAV to: {temp_path}")
            
            # synthesize_wav needs a file opened in WAV mode
            import wave as wave_module
            with wave_module.open(temp_path, 'wb') as wav_file:
                wav_file.setnchannels(1)  # Mono
                wav_file.setsampwidth(2)  # 16-bit
                wav_file.setframerate(voice.config.sample_rate)  # Use voice's sample rate
                
                # Now call synthesize_wav with the wave file and synthesis config
                voice.synthesize_wav(text, wav_file, syn_config=syn_config)
            
            # Read the generated file
            if os.path.exists(temp_path):
                file_size = os.path.getsize(temp_path)
                
                if file_size > 44:  # Valid WAV file
                    with open(temp_path, "rb") as f:
                        wav_data = f.read()
                    
                    os.unlink(temp_path)  # Clean up
                    return Response(content=wav_data, media_type="audio/wav")
                else:
                    os.unlink(temp_path)
            
        except Exception as method1_error:
            print(f"[Piper] Method 1 (synthesize_wav) failed: {method1_error}")
            import traceback
            print(f"[Piper] Method 1 traceback: {traceback.format_exc()}")
        
        # Method 2: Try basic synthesize to WAV file buffer (no speaker support - LAST RESORT)
        try:
            
            # Create WAV file in memory
            import wave as wave_module
            audio_buffer = io.BytesIO()
            with wave_module.open(audio_buffer, 'wb') as wav_file:
                wav_file.setnchannels(1)  # Mono
                wav_file.setsampwidth(2)  # 16-bit
                wav_file.setframerate(voice.config.sample_rate)
                
                # Try basic synthesize with synthesis config
                voice.synthesize(text, wav_file, syn_config=syn_config)
            
            wav_data = audio_buffer.getvalue()
            
            if wav_data and len(wav_data) > 44:
                return Response(content=wav_data, media_type="audio/wav")
                
        except Exception as method2_error:
            print(f"[Piper] Method 2 (basic synthesize) failed: {method2_error}")
            import traceback
            print(f"[Piper] Method 2 traceback: {traceback.format_exc()}")
        
        # If all methods fail, return silence
        print(f"[Piper] ERROR: All synthesis methods failed for text: '{text[:50]}...'")
        return create_silent_wav(0.1)
        
    except Exception as e:
        print(f"[Piper] CRITICAL ERROR in tts_endpoint: {e}")
        import traceback
        print(f"[Piper] Critical error traceback: {traceback.format_exc()}")
        return create_silent_wav(0.1)


def create_silent_wav(duration_seconds=0.1):
    """Create a silent WAV file of specified duration"""
    import wave as wave_module
    sample_rate = 22050  # Piper's default sample rate
    samples = int(sample_rate * duration_seconds)
    
    buffer = io.BytesIO()
    with wave_module.open(buffer, 'wb') as wav_file:
        wav_file.setnchannels(1)  # Mono
        wav_file.setsampwidth(2)  # 16-bit
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b'\x00' * (samples * 2))  # Silent audio
    
    return Response(content=buffer.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8011)