"""Measure known generated tones without using ASR output as reference truth."""

import argparse
import array
import hashlib
import json
import math
import pathlib
import statistics
import wave


def amplitude(samples, sample_rate, frequency):
    """Return the single-frequency Goertzel amplitude of one complete second."""
    coefficient = 2 * math.cos(2 * math.pi * frequency / sample_rate)
    previous = before_previous = 0.0
    for sample in samples:
        current = sample / 32768.0 + coefficient * previous - before_previous
        before_previous, previous = previous, current
    power = previous * previous + before_previous * before_previous - coefficient * previous * before_previous
    return 2 * math.sqrt(max(0.0, power)) / len(samples)


def inspect(path, target_frequency, excluded_frequency):
    """Measure each complete one-second interval and retain file provenance."""
    amplitudes = {target_frequency: [], excluded_frequency: []}
    with wave.open(str(path), "rb") as source:
        if source.getsampwidth() != 2:
            raise ValueError("Expected PCM16 WAV")
        sample_rate, channels, frames = source.getframerate(), source.getnchannels(), source.getnframes()
        while True:
            raw = source.readframes(sample_rate)
            if len(raw) < sample_rate * channels * 2:
                break
            samples = array.array("h", raw)[::channels]
            for frequency in amplitudes:
                amplitudes[frequency].append(amplitude(samples, sample_rate, frequency))
    medians = {frequency: statistics.median(values) if values else 0.0 for frequency, values in amplitudes.items()}
    target, independent = medians[target_frequency], medians[excluded_frequency]
    ratio_db = 20 * math.log10(max(independent, 1e-15) / max(target, 1e-15))
    return {
        "file": str(path.resolve()),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "sampleRate": sample_rate,
        "channels": channels,
        "frames": frames,
        "completeSeconds": len(amplitudes[target_frequency]),
        "targetFrequencyHz": target_frequency,
        "excludedFrequencyHz": excluded_frequency,
        "medianAmplitudes": medians,
        "independentRelativeDb": ratio_db,
        "criterion": "target amplitude > 0.001 and independent/target < -40 dB; generated tones only",
        "passed": target > 0.001 and ratio_db < -40,
    }


def main():
    """Write a reproducible content check beside the explicit recording."""
    parser = argparse.ArgumentParser()
    parser.add_argument("wave", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--target-frequency", type=int, default=440)
    parser.add_argument("--excluded-frequency", type=int, default=997)
    arguments = parser.parse_args()
    evidence = inspect(arguments.wave, arguments.target_frequency, arguments.excluded_frequency)
    result = json.dumps(evidence, ensure_ascii=False, indent=2) + "\n"
    if arguments.output:
        arguments.output.write_text(result, encoding="utf-8", newline="\n")
    print(result, end="")
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
