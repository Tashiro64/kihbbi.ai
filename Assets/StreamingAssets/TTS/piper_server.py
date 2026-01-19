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
        print(f"Found model file: {model_path}")
        
        # Try loading with explicit encoding handling
        try:
            voice = PiperVoice.load(model_path)
        except UnicodeEncodeError as enc_error:
            print(f"Encoding error with path: {enc_error}")
            # Try with different path handling
            model_path_short = os.path.basename(model_path)
            print(f"Trying with short path: {model_path_short}")
            # Change to the directory and use relative path
            original_dir = os.getcwd()
            os.chdir(BASE_DIR)
            try:
                voice = PiperVoice.load(model_path_short)
                print("Successfully loaded with short path")
            except Exception as e2:
                print(f"Short path also failed: {e2}")
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
        print("Downloading model file...")
        urllib.request.urlretrieve(model_url, model_path)
        print("Downloading config file...")  
        urllib.request.urlretrieve(config_url, config_path)
        
        print(f"Downloaded model to: {model_path}")
        print(f"Config file at: {config_path}")
        
        # Verify files exist and are not empty
        if os.path.exists(model_path):
            print(f"Model file size: {os.path.getsize(model_path)} bytes")
        if os.path.exists(config_path):
            print(f"Config file size: {os.path.getsize(config_path)} bytes")
        
        # Change to the directory to avoid path encoding issues
        original_dir = os.getcwd()
        os.chdir(BASE_DIR)
        try:
            voice = PiperVoice.load("en_US-libritts_r-medium.onnx")  # Use relative path
            print("Successfully loaded downloaded model")
        except Exception as load_error:
            print(f"Failed to load downloaded model: {load_error}")
        finally:
            os.chdir(original_dir)
    
    print(f"Piper voice loaded successfully: {voice}")
    print(f"Voice attributes: {dir(voice)}")
    
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
    syn_config = SynthesisConfig(
        speaker_id=speaker_id,
        length_scale=length_scale,
        normalize_audio=True,
        volume=1.0
    )
    
    if not text:
        print("[Piper] ERROR: Empty text received")
        return create_silent_wav(0.1)
    
    # Basic text cleaning
    text = text.strip()
    if len(text) < 2:
        print("[Piper] ERROR: Text too short")
        return create_silent_wav(0.1)
    
    try:
        # Generate audio using Piper
        print(f"[Piper] Attempting synthesis with voice: {voice}")
        print(f"[Piper] Voice type: {type(voice)}")
        print(f"[Piper] Text to synthesize: '{text}'")
        print(f"[Piper] Using speaker_id: {speaker_id}")
        print(f"[Piper] Voice methods: {[m for m in dir(voice) if not m.startswith('_')]}")
        
        # Method 3: Try manual phoneme approach with speaker support (PRIORITY METHOD)
        try:
            print(f"[Piper] Trying manual phoneme synthesis with speaker_id={speaker_id}...")
            
            # Get phonemes from text
            phonemes = voice.phonemize(text)
            print(f"[Piper] Generated phonemes: {phonemes[:50]}...")  # Show first 50 chars
            
            # Convert phonemes to IDs - handle the list structure properly
            if hasattr(voice, 'phonemes_to_ids'):
                phoneme_ids = voice.phonemes_to_ids(phonemes)
                print(f"[Piper] Generated {len(phoneme_ids)} phoneme IDs")
                
                # Convert phonemes to audio WITH synthesis config
                try:
                    audio_data = voice.phoneme_ids_to_audio(phoneme_ids, syn_config=syn_config)
                    print(f"[Piper] Used speaker_id={speaker_id}, length_scale={length_scale} for synthesis")
                except Exception as config_error:
                    print(f"[Piper] SynthesisConfig failed: {config_error}, trying basic method")
                    try:
                        audio_data = voice.phoneme_ids_to_audio(phoneme_ids)
                        print(f"[Piper] Used basic synthesis (no speaker/rate control)")
                    except Exception as basic_error:
                        print(f"[Piper] Basic synthesis also failed: {basic_error}")
                        audio_data = None
                
                if audio_data and len(audio_data) > 0:
                    # Convert raw audio to WAV format
                    audio_buffer = io.BytesIO()
                    with wave.open(audio_buffer, 'wb') as wav_file:
                        wav_file.setnchannels(1)  # Mono
                        wav_file.setsampwidth(2)  # 16-bit
                        wav_file.setframerate(voice.config.sample_rate)
                        
                        # Ensure audio_data is bytes
                        if isinstance(audio_data, (list, tuple, tuple)):
                            # Convert numpy array or list to bytes
                            import numpy as np
                            if hasattr(audio_data, 'dtype'):
                                # It's already a numpy array
                                audio_bytes = audio_data.tobytes()
                            else:
                                # Convert list/tuple to numpy array then bytes
                                audio_array = np.array(audio_data, dtype=np.float32)
                                # Normalize and convert to int16
                                audio_int16 = (audio_array * 32767).astype(np.int16)
                                audio_bytes = audio_int16.tobytes()
                        else:
                            audio_bytes = audio_data
                            
                        wav_file.writeframes(audio_bytes)
                    
                    wav_data = audio_buffer.getvalue()
                    print(f"[Piper] SUCCESS (manual with speaker): Generated {len(wav_data)} bytes WAV from {len(audio_data)} audio samples")
                    return Response(content=wav_data, media_type="audio/wav")
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
            import wave
            with wave.open(temp_path, 'wb') as wav_file:
                wav_file.setnchannels(1)  # Mono
                wav_file.setsampwidth(2)  # 16-bit
                wav_file.setframerate(voice.config.sample_rate)  # Use voice's sample rate
                
                # Now call synthesize_wav with the wave file and synthesis config
                voice.synthesize_wav(text, wav_file, syn_config=syn_config)
            
            # Read the generated file
            if os.path.exists(temp_path):
                file_size = os.path.getsize(temp_path)
                print(f"[Piper] Generated WAV file size: {file_size} bytes")
                
                if file_size > 44:  # Valid WAV file
                    with open(temp_path, "rb") as f:
                        wav_data = f.read()
                    
                    os.unlink(temp_path)  # Clean up
                    print(f"[Piper] SUCCESS (synthesize_wav fallback): Generated {len(wav_data)} bytes - NO SPEAKER SELECTION")
                    return Response(content=wav_data, media_type="audio/wav")
                else:
                    print(f"[Piper] WAV file too small or empty")
                    os.unlink(temp_path)
            
        except Exception as method1_error:
            print(f"[Piper] synthesize_wav failed: {method1_error}")
            print(f"[Piper] Error type: {type(method1_error)}")
        
        # Method 2: Try basic synthesize to WAV file buffer (no speaker support - LAST RESORT)
        try:
            print(f"[Piper] Trying synthesize buffer fallback (no speaker selection)...")
            
            # Create WAV file in memory
            audio_buffer = io.BytesIO()
            with wave.open(audio_buffer, 'wb') as wav_file:
                wav_file.setnchannels(1)  # Mono
                wav_file.setsampwidth(2)  # 16-bit
                wav_file.setframerate(voice.config.sample_rate)
                
                # Try basic synthesize with synthesis config
                voice.synthesize(text, wav_file, syn_config=syn_config)
            
            wav_data = audio_buffer.getvalue()
            print(f"[Piper] Buffer synthesize result: {len(wav_data)} bytes")
            
            if wav_data and len(wav_data) > 44:
                print(f"[Piper] SUCCESS (buffer fallback): Generated {len(wav_data)} bytes - NO SPEAKER SELECTION")
                return Response(content=wav_data, media_type="audio/wav")
                
        except Exception as method2_error:
            print(f"[Piper] Buffer synthesize failed: {method2_error}")
            print(f"[Piper] Error type: {type(method2_error)}")
        
        # If all methods fail, return silence
        print("[Piper] ERROR: All synthesis methods failed - returning silence")
        return create_silent_wav(0.1)
        
    except Exception as e:
        print(f"[Piper] ERROR: {type(e).__name__}: {e}")
        print(f"[Piper] Text that caused error: '{text}'")
        return create_silent_wav(0.1)


def create_silent_wav(duration_seconds=0.1):
    """Create a silent WAV file of specified duration"""
    sample_rate = 22050  # Piper's default sample rate
    samples = int(sample_rate * duration_seconds)
    
    buffer = io.BytesIO()
    with wave.open(buffer, 'wb') as wav_file:
        wav_file.setnchannels(1)  # Mono
        wav_file.setsampwidth(2)  # 16-bit
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b'\x00' * (samples * 2))  # Silent audio
    
    return Response(content=buffer.getvalue(), media_type="audio/wav")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8011)