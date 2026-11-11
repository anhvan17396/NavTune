#!/usr/bin/env python3
"""Converts Google's YAMNet weights into the compact model file NavTune for Windows embeds.

YAMNet (Apache License 2.0) is the AudioSet sound classifier from
https://github.com/tensorflow/models/tree/master/research/audioset/yamnet.
NavTune uses it where the macOS version uses Apple's built-in SoundAnalysis classifier.

    python convert_yamnet.py yamnet.h5 ../../src/NavTune.Core/Resources/yamnet.bin [clip16k.wav ...]

The script
* folds every batch normalization into the convolution before it,
* stores kernels as float16 and biases as float32 (about 7.5 MB instead of 15 MB),
* checks the folded model against the original float32 weights, using a NumPy port of the
  official yamnet.py and features.py (no TensorFlow needed),
* prints the reference values the unit tests compare the C# port with,
* prints the top classes of any 16 kHz mono 16-bit WAV given, as a sanity check.

Needs numpy and h5py. The inputs come from Google:
    curl -O https://storage.googleapis.com/audioset/yamnet.h5
    curl -O https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv
"""

import csv
import struct
import sys
import wave
from pathlib import Path

import h5py
import numpy as np

# params.py
SAMPLE_RATE = 16000
WINDOW, HOP, FFT = 400, 160, 512
MEL_BANDS, MEL_MIN_HZ, MEL_MAX_HZ, LOG_OFFSET = 64, 125.0, 7500.0, 0.001
PATCH_FRAMES, PATCH_HOP_FRAMES = 96, 48
PATCH_SAMPLES = (PATCH_FRAMES - 1) * HOP + WINDOW  # 15600 samples = 0.975 s
EPSILON = 1e-4
# yamnet.py: (stride, filters) of layer1 (a full 3x3 convolution) and layer2...layer14 (separable).
LAYERS = [(2, 32), (1, 64), (2, 128), (1, 128), (2, 256), (1, 256), (2, 512),
          (1, 512), (1, 512), (1, 512), (1, 512), (1, 512), (2, 1024), (1, 1024)]


# MARK: - Features (features.py)

def hz_to_mel(hz):
    return 1127.0 * np.log(1.0 + hz / 700.0)


def mel_matrix():
    """tf.signal.linear_to_mel_weight_matrix: HTK mel scale, the DC bin is left out."""
    bins = FFT // 2 + 1
    bin_mel = hz_to_mel(np.linspace(0.0, SAMPLE_RATE / 2, bins)[1:])[:, None]
    edges = np.linspace(hz_to_mel(MEL_MIN_HZ), hz_to_mel(MEL_MAX_HZ), MEL_BANDS + 2)
    lower, center, upper = edges[:-2], edges[1:-1], edges[2:]
    weights = np.maximum(0.0, np.minimum((bin_mel - lower) / (center - lower),
                                         (upper - bin_mel) / (upper - center)))
    return np.vstack([np.zeros((1, MEL_BANDS)), weights])


def log_mel(waveform):
    """log(|STFT| x mel + 0.001) with a periodic Hann window, 25 ms frames every 10 ms."""
    frames = 1 + (len(waveform) - WINDOW) // HOP
    window = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(WINDOW) / WINDOW)
    framed = np.stack([waveform[i * HOP:i * HOP + WINDOW] for i in range(frames)]) * window
    magnitude = np.abs(np.fft.rfft(framed, n=FFT))
    return np.log(magnitude @ mel_matrix() + LOG_OFFSET)


def patches(waveform):
    """pad_waveform + waveform_to_log_mel_spectrogram_patches: 0.96 s patches every 0.48 s."""
    hop_samples = PATCH_HOP_FRAMES * HOP
    count = max(len(waveform), PATCH_SAMPLES)
    count = PATCH_SAMPLES + -(-(count - PATCH_SAMPLES) // hop_samples) * hop_samples
    spectrogram = log_mel(np.pad(waveform, (0, count - len(waveform))))
    starts = range(0, len(spectrogram) - PATCH_FRAMES + 1, PATCH_HOP_FRAMES)
    return [spectrogram[s:s + PATCH_FRAMES] for s in starts]


# MARK: - Network (yamnet.py)

def same_padding(size, stride):
    """TensorFlow 'same' padding for a 3-tap kernel: (output size, before, after)."""
    out = -(-size // stride)
    total = max((out - 1) * stride + 3 - size, 0)
    return out, total // 2, total - total // 2


def conv3x3(x, kernel, stride, depthwise):
    h, w, _ = x.shape
    oh, top, bottom = same_padding(h, stride)
    ow, left, right = same_padding(w, stride)
    padded = np.pad(x, [(top, bottom), (left, right), (0, 0)])
    out = 0.0
    for i in range(3):
        for j in range(3):
            taps = padded[i:i + stride * oh:stride, j:j + stride * ow:stride, :]
            out = out + (taps * kernel[i, j] if depthwise else taps @ kernel[i, j])
    return out


def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-x))


class Weights:
    def __init__(self, path):
        self.file = h5py.File(path, "r")

    def get(self, name):
        return np.array(self.file[name], dtype=np.float64)

    def kernel(self, layer, kind="kernel"):
        return self.get(f"{layer}/{layer}/{kind}:0")

    def batch_norm(self, layer):
        prefix = f"{layer}/bn/{layer}/bn/"
        return (self.get(prefix + "beta:0"), self.get(prefix + "moving_mean:0"),
                self.get(prefix + "moving_variance:0"))


def original_scores(weights, patch):
    """The unmodified model: convolution, batch norm (center only, no scale), ReLU."""
    def bn_relu(x, layer):
        beta, mean, variance = weights.batch_norm(layer)
        return np.maximum((x - mean) / np.sqrt(variance + EPSILON) + beta, 0.0)

    x = bn_relu(conv3x3(patch[:, :, None], weights.kernel("layer1/conv"), 2, False), "layer1/conv")
    for i, (stride, _) in enumerate(LAYERS[1:], start=2):
        dw, pw = f"layer{i}/depthwise_conv", f"layer{i}/pointwise_conv"
        x = bn_relu(conv3x3(x, weights.kernel(dw, "depthwise_kernel")[:, :, :, 0], stride, True), dw)
        x = bn_relu(x @ weights.kernel(pw)[0, 0], pw)
    logits = x.mean(axis=(0, 1)) @ weights.get("logits/logits/kernel:0") + weights.get("logits/logits/bias:0")
    return sigmoid(logits)


def fold(weights):
    """Returns NavTune's tensors: kernels with the batch norm folded in, plus a bias each."""
    def folded(kernel, layer):
        beta, mean, variance = weights.batch_norm(layer)
        scale = 1.0 / np.sqrt(variance + EPSILON)
        return kernel * scale, beta - mean * scale  # the last axis of every kernel is the output channel

    tensors = []
    w, b = folded(weights.kernel("layer1/conv")[:, :, 0, :], "layer1/conv")  # [ky][kx][out]
    tensors += [("conv1.w", w), ("conv1.b", b)]
    for i in range(2, len(LAYERS) + 1):
        dw, pw = f"layer{i}/depthwise_conv", f"layer{i}/pointwise_conv"
        w, b = folded(weights.kernel(dw, "depthwise_kernel")[:, :, :, 0], dw)  # [ky][kx][channel]
        tensors += [(f"l{i}.dw.w", w), (f"l{i}.dw.b", b)]
        w, b = folded(weights.kernel(pw)[0, 0], pw)  # [in][out]
        tensors += [(f"l{i}.pw.w", w), (f"l{i}.pw.b", b)]
    tensors += [("logits.w", weights.get("logits/logits/kernel:0")),  # [1024][521]
                ("logits.b", weights.get("logits/logits/bias:0"))]
    # What the app will actually run with: kernels rounded to float16, biases to float32.
    return [(n, t.astype(np.float32 if n.endswith(".b") else np.float16).astype(np.float64)) for n, t in tensors]


def folded_scores(t, patch):
    x = np.maximum(conv3x3(patch[:, :, None], t["conv1.w"][:, :, None, :], 2, False) + t["conv1.b"], 0.0)
    for i, (stride, _) in enumerate(LAYERS[1:], start=2):
        x = np.maximum(conv3x3(x, t[f"l{i}.dw.w"], stride, True) + t[f"l{i}.dw.b"], 0.0)
        x = np.maximum(x @ t[f"l{i}.pw.w"] + t[f"l{i}.pw.b"], 0.0)
    return sigmoid(x.mean(axis=(0, 1)) @ t["logits.w"] + t["logits.b"])


def write(path, tensors):
    """Little-endian: "NTYAMNET", u32 version, u32 count, then per tensor:
    u8 name length, name, u8 type (1 = float16, 2 = float32), u8 rank, u32 dims[rank], data."""
    with open(path, "wb") as f:
        f.write(b"NTYAMNET")
        f.write(struct.pack("<II", 1, len(tensors)))
        for name, value in tensors:
            bias = name.endswith(".b")
            data = value.astype("<f4" if bias else "<f2")
            assert np.all(np.isfinite(data)), name
            f.write(struct.pack("<B", len(name)) + name.encode("ascii"))
            f.write(struct.pack("<BB", 2 if bias else 1, value.ndim))
            f.write(struct.pack(f"<{value.ndim}I", *value.shape))
            f.write(data.tobytes())


# MARK: - Checks

def test_signals():
    """Deterministic inputs the C# unit tests rebuild sample for sample."""
    n = np.arange(PATCH_SAMPLES, dtype=np.float64)
    sine = 0.5 * np.sin(2 * np.pi * 440 * n / SAMPLE_RATE)
    noise = np.empty(PATCH_SAMPLES)
    state = 12345
    for i in range(PATCH_SAMPLES):
        state = (state * 1103515245 + 12345) % 2**31
        noise[i] = (state / 2**31 * 2 - 1) * 0.3
    buzz = sum(0.3 / k * np.sin(2 * np.pi * 220 * k * n / SAMPLE_RATE) for k in range(1, 21))
    buzz *= 0.5 + 0.5 * np.sin(2 * np.pi * 3 * n / SAMPLE_RATE)
    return {"Sine440": sine, "Noise": noise, "Buzz": buzz}


def read_wav(path):
    with wave.open(str(path), "rb") as w:
        assert w.getframerate() == SAMPLE_RATE and w.getnchannels() == 1 and w.getsampwidth() == 2, path
        return np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float64) / 32768.0


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    weights = Weights(sys.argv[1])
    out = Path(sys.argv[2])
    names = [row[2] for row in list(csv.reader(open(Path(sys.argv[1]).with_name("yamnet_class_map.csv"), encoding="utf-8")))[1:]]

    tensors = fold(weights)
    write(out, tensors)
    t = dict(tensors)
    print(f"Wrote {out} ({out.stat().st_size / 1e6:.2f} MB, {sum(v.size for _, v in tensors):,} weights)")

    for name, signal in test_signals().items():
        signal = signal.astype(np.float32).astype(np.float64)
        features = log_mel(signal)
        original = original_scores(weights, features)
        folded = folded_scores(t, features)
        print(f"\n{name}: folded float16 vs original float32, largest score difference {np.abs(folded - original).max():.2e}")
        print(f"  log-mel [0,0]={features[0, 0]:.6f} [10,5]={features[10, 5]:.6f} [50,20]={features[50, 20]:.6f} [95,63]={features[95, 63]:.6f}")
        top = np.argsort(folded)[::-1][:5]
        print("  top: " + ", ".join(f"({i}, {folded[i]:.6f}f)" for i in top) + "   // " + ", ".join(names[i] for i in top))

    for path in sys.argv[3:]:
        scores = np.mean([folded_scores(t, p) for p in patches(read_wav(path))], axis=0)
        top = np.argsort(scores)[::-1][:8]
        print(f"\n{Path(path).name}: " + ", ".join(f"{names[i]} {scores[i]:.3f}" for i in top))


if __name__ == "__main__":
    main()
