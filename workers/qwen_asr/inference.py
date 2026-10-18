"""Offline single-window Qwen adapter preserving unmodified generated text."""

import pathlib
import time
import wave

from workers.qwen_asr.output import parse_output


def digital_silence(audio):
    """Recognize only exact zero PCM; never threshold quiet or repeated speech."""
    import numpy as np
    return audio.size > 0 and not np.any(audio)


def read_audio(path, max_window_seconds=30):
    """Read a complete 16k mono PCM16 window, rejecting rather than cutting length."""
    import numpy as np
    if not 1 <= max_window_seconds <= 1200:
        raise ValueError("Evaluation window budget must be in [1,1200] seconds")
    path = pathlib.Path(path)
    if not path.is_absolute():
        raise ValueError("Local absolute audio path required")
    with wave.open(str(path), "rb") as audio:
        frames = audio.getnframes()
        if (audio.getframerate(), audio.getnchannels(), audio.getsampwidth()) != (16000, 1, 2):
            raise ValueError("Expected 16kHz mono PCM16 WAV; prepare explicitly with FFmpeg")
        if not 0 < frames <= max_window_seconds * 16000:
            raise ValueError("Audio exceeds the explicit evaluation window budget; no implicit slicing")
        data = audio.readframes(frames)
        if len(data) != frames * 2:
            raise ValueError("Truncated WAV payload")
    return np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0


class QwenInference:
    """Keep one local model in a serial evaluation process, exposing EOS status."""

    def __init__(self, model_directory, max_new_tokens=512, max_window_seconds=30):
        """Load BF16/SDPA from already verified local assets, with no CPU fallback."""
        import torch
        from qwen_asr import Qwen3ASRModel
        if not 1 <= max_new_tokens <= 4096:
            raise ValueError("Token budget must be in [1,4096]")
        self.max_new_tokens = max_new_tokens
        if not 1 <= max_window_seconds <= 1200:
            raise ValueError("Evaluation window budget must be in [1,1200] seconds")
        self.max_window_seconds = max_window_seconds
        self.asr = Qwen3ASRModel.from_pretrained(
            str(model_directory), dtype=torch.bfloat16, device_map="cuda:0",
            attn_implementation="sdpa", local_files_only=True,
            max_inference_batch_size=1, max_new_tokens=max_new_tokens)

    def recognize(self, audio, language="Japanese"):
        """Generate the complete window; never call upstream repetition correction."""
        import numpy as np
        import torch
        if audio.ndim != 1 or not 0 < audio.size <= self.max_window_seconds * 16000 or not np.isfinite(audio).all():
            raise ValueError("Invalid bounded mono audio window")
        if language not in (None, "Japanese", "English", "Chinese"):
            raise ValueError("Unsupported evaluation language")
        if digital_silence(audio):
            return {"raw": "", "text": "", "language": language, "generatedTokens": 0,
                    "endedWithEos": False, "truncated": False, "complete": True,
                    "termination": "exact-digital-silence", "inferenceExecuted": False,
                    "noSpeech": True,
                    "elapsedSeconds": 0.0, "audioSeconds": len(audio) / 16000,
                    "sampleStart": 0, "sampleEnd": len(audio), "peakAllocatedBytes": None,
                    "peakReservedBytes": None, "rtf": 0.0}
        messages = [{"role": "system", "content": ""},
                    {"role": "user", "content": [{"type": "audio", "audio": ""}]}]
        prompt = self.asr.processor.apply_chat_template(messages, add_generation_prompt=True, tokenize=False)
        if language:
            prompt += f"language {language}<asr_text>"
        torch.cuda.synchronize()
        torch.cuda.reset_peak_memory_stats()
        started = time.perf_counter()
        inputs = self.asr.processor(text=[prompt], audio=[audio], return_tensors="pt", padding=True)
        inputs = inputs.to(self.asr.model.device).to(self.asr.model.dtype)
        with torch.inference_mode():
            generated = self.asr.model.generate(**inputs, max_new_tokens=self.max_new_tokens, do_sample=False)
        torch.cuda.synchronize()
        ids = generated.sequences[0, inputs["input_ids"].shape[1]:].tolist()
        raw = self.asr.processor.batch_decode([ids], skip_special_tokens=True,
                                              clean_up_tokenization_spaces=False)[0]
        eos = self.asr.model.generation_config.eos_token_id
        if isinstance(eos, int):
            eos = [eos]
        result = parse_output(raw, language, ids, eos or [], self.max_new_tokens)
        result.update(termination="model-no-speech" if result["noSpeech"] else ("model-eos" if result["complete"] else "incomplete-generation"), inferenceExecuted=True)
        result.update(elapsedSeconds=time.perf_counter() - started, audioSeconds=len(audio) / 16000,
                      sampleStart=0, sampleEnd=len(audio),
                      peakAllocatedBytes=torch.cuda.max_memory_allocated(),
                      peakReservedBytes=torch.cuda.max_memory_reserved())
        result["rtf"] = result["elapsedSeconds"] / result["audioSeconds"]
        return result
