"""
Jarvis voice engine for Dynamic Island.

  wake word  : openWakeWord  ("hey jarvis")
  speech->text: faster-whisper (base.en, int8, CPU)
  text->speech: Kokoro (onnx, voice bm_george)

Talks to the island over stdin/stdout, one JSON object per line.
  out: {"event":"ready"} {"event":"wake"} {"event":"speech"} {"event":"heard","text":..}
       {"event":"nothing"} {"event":"speaking"} {"event":"spoken"} {"event":"level","value":0..1} {"event":"error","message":..}
  in : {"cmd":"say","text":..} {"cmd":"stop"} {"cmd":"listen"} {"cmd":"quit"}
The island owns the brain (Claude Code + MCP); this process only hears and speaks.
"""
import json, os, queue, sys, threading, time

import numpy as np
import sounddevice as sd

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(HERE, "models")
RATE, FRAME = 16000, 1280  # openWakeWord wants 80 ms frames of 16 kHz int16

out_lock = threading.Lock()


def emit(event, **kw):
    with out_lock:
        sys.stdout.write(json.dumps({"event": event, **kw}) + "\n")
        sys.stdout.flush()


def log(msg):
    sys.stderr.write(time.strftime("%H:%M:%S ") + msg + "\n")
    sys.stderr.flush()


# ---------------- models ----------------
from openwakeword.model import Model as WakeModel
from faster_whisper import WhisperModel
from kokoro_onnx import Kokoro

wake = WakeModel(
    wakeword_models=[os.path.join(MODELS, "hey_jarvis_v0.1.onnx")],
    inference_framework="onnx",
    melspec_model_path=os.path.join(MODELS, "melspectrogram.onnx"),
    embedding_model_path=os.path.join(MODELS, "embedding_model.onnx"),
)
whisper = WhisperModel("base.en", device="cpu", compute_type="int8", download_root=os.path.join(MODELS, "whisper"))
tts = Kokoro(os.path.join(MODELS, "kokoro-v1.0.int8.onnx"), os.path.join(MODELS, "voices-v1.0.bin"))
VOICE = os.environ.get("JARVIS_VOICE", "bm_george")
DEBUG = os.environ.get("JARVIS_DEBUG") == "1"

# ---------------- audio in ----------------
frames: "queue.Queue[np.ndarray]" = queue.Queue()


def on_audio(indata, n, t, status):
    frames.put(indata[:, 0].copy())


class WavMic:
    """Test mode (JARVIS_TEST_WAV=file.wav, 16 kHz mono): plays a recording into the pipeline as if spoken."""
    def __init__(self, path):
        import soundfile as sf
        self.data, _ = sf.read(path, dtype="int16")

    def start(self):
        def run():
            time.sleep(1)
            for i in range(0, len(self.data), FRAME):
                c = self.data[i:i + FRAME]
                frames.put(np.pad(c, (0, FRAME - len(c))))
                time.sleep(0.08)
            while True:  # silence afterwards
                frames.put(np.zeros(FRAME, np.int16))
                time.sleep(0.08)
        threading.Thread(target=run, daemon=True).start()


TEST_WAV = os.environ.get("JARVIS_TEST_WAV")
mic = WavMic(TEST_WAV) if TEST_WAV else sd.InputStream(samplerate=RATE, channels=1, dtype="int16", blocksize=FRAME, callback=on_audio)

speaking = threading.Event()
listen_now = threading.Event()
stop_speech = threading.Event()
gain = 1.0  # auto gain for quiet microphones


def agc(chunk: np.ndarray) -> np.ndarray:
    """Slow automatic gain so quiet mics still reach the wake model at a healthy level."""
    global gain
    peak = float(np.abs(chunk).max()) + 1.0
    target = 12000.0
    if peak * gain > 30000:
        gain = max(1.0, 30000.0 / peak)
    elif peak > 150:  # only adapt on real sound, not hiss
        gain = min(40.0, gain * 0.7 + (target / peak) * 0.3)
    return np.clip(chunk.astype(np.float32) * gain, -32768, 32767).astype(np.int16)


def record_utterance():
    """Energy VAD with an adaptive noise floor. Returns float32 audio or None."""
    got, floor, speaking_now, silent, total = [], None, False, 0, 0
    start = 0
    while True:
        try:
            c = frames.get(timeout=1)
        except queue.Empty:
            continue
        x = c.astype(np.float32) / 32768.0
        got.append(x)
        rms = float(np.sqrt(np.mean(x * x)))
        total += 80
        emit("level", value=round(min(1.0, rms * gain * 8), 3))
        floor = rms if floor is None else (floor * 0.9 + rms * 0.1 if not speaking_now else floor)
        thr = max(floor * 2.5, 0.0012)
        if not speaking_now and rms > thr and total > 160:
            speaking_now, start = True, max(0, len(got) - 5)
            emit("speech")
        elif speaking_now:
            silent = 0 if rms > thr * 0.8 else silent + 80
            if silent >= 1200 or total > 15000:
                break
        if not speaking_now and total > 6000:
            return None
    audio = np.concatenate(got[start:])
    peak = float(np.abs(audio).max())
    if peak > 0:
        audio *= min(30.0, 0.9 / peak)
    return audio


HALLUCINATIONS = {"", "you", "you.", "thank you.", "thanks for watching!", "bye."}


def transcribe(audio):
    segs, _ = whisper.transcribe(
        audio, language="en", beam_size=1, vad_filter=False,
        initial_prompt="Hey Jarvis. Open Google Chrome. What's on my screen? Where do I click to export?")
    text = "".join(s.text for s in segs).strip()
    return None if text.lower() in HALLUCINATIONS or text.startswith(("[", "(")) else text


def drain():
    while not frames.empty():
        frames.get_nowait()


def say(text):
    stop_speech.clear()
    speaking.set()
    emit("speaking")
    try:
        # Sentence by sentence so speech starts fast and can be interrupted.
        import re
        for sentence in [s for s in re.split(r"(?<=[.!?])\s+", text) if s.strip()]:
            if stop_speech.is_set():
                break
            samples, sr = tts.create(sentence, voice=VOICE, speed=1.05, lang="en-gb")
            sd.play(samples, sr)
            while sd.get_stream().active:
                if stop_speech.is_set():
                    sd.stop()
                    break
                time.sleep(0.03)
    except Exception as e:  # keep the engine alive whatever the TTS does
        emit("error", message="speech failed: " + str(e))
    finally:
        speaking.clear()
        drain()  # don't wake on our own voice
        wake.reset()
        emit("spoken")


def commands():
    while True:
        line = sys.stdin.readline()  # not `for line in sys.stdin`: that read-ahead buffers on Windows
        if not line:
            break
        line = line.lstrip("﻿")  # some writers prepend a UTF-8 BOM
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        cmd = msg.get("cmd")
        if cmd == "say":
            threading.Thread(target=say, args=(msg.get("text", ""),), daemon=True).start()
        elif cmd == "stop":
            stop_speech.set()
        elif cmd == "listen":
            stop_speech.set()
            listen_now.set()
        elif cmd == "quit":
            os._exit(0)
    os._exit(0)  # island went away


def main():
    threading.Thread(target=commands, daemon=True).start()
    mic.start()
    emit("ready", voice=VOICE)
    log("ready")
    while True:
        try:
            c = frames.get(timeout=0.5)
        except queue.Empty:
            c = None
        woke = listen_now.is_set()
        if c is not None and not speaking.is_set() and not woke:
            boosted = agc(c)
            score = wake.predict(boosted).get("hey_jarvis_v0.1", 0.0)
            if DEBUG and (score > 0.05 or np.abs(c).max() > 300):
                log(f"peak {int(np.abs(c).max())} gain {gain:.1f} score {score:.2f}")
            if score > 0.35:
                log(f"wake {score:.2f}")
                woke = True
        if not woke:
            continue
        listen_now.clear()
        wake.reset()
        emit("wake")
        audio = record_utterance()
        text = transcribe(audio) if audio is not None else None
        log(f"heard: {text!r}")
        emit("heard", text=text) if text else emit("nothing")
        drain()


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        emit("error", message=str(e))
        raise
