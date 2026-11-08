#!/usr/bin/env python3
"""Local English -> Chinese translation worker for ModManager.

Speaks a line-delimited JSON protocol over stdin/stdout, so the WPF process never loads
Python or native inference libraries in-process. One process is kept alive because loading
the model once is what makes the second and later translations cheap.

Engine
------
CTranslate2 + SentencePiece over an official Argos Translate package. The inference call
mirrors ``argostranslate.translate.apply_packaged_translation`` exactly:

    translator.translate_batch(
        tokenized,
        target_prefix=...,
        replace_unknowns=True,
        max_batch_size=32,
        batch_type="tokens",
        beam_size=4,
        num_hypotheses=1,
        length_penalty=0.2,
        return_scores=True,
    )

and the decoding mirrors Argos' ``SentencePieceTokenizer.decode`` (``decode_pieces`` with
the U+2581 marker and ``_`` mapped back to spaces).

Why not ``import argostranslate``
---------------------------------
``argostranslate.sbd`` imports ``stanza`` unconditionally at module scope, and stanza pulls
in torch plus spacy: roughly 1 GB of dependencies whose only job here would be sentence
boundary detection. ModManager segments text itself (see TranslationSegmenter in C#), which
it must do anyway to keep paragraph order, Markdown structure and protected tokens intact.
So the worker uses the same two libraries Argos itself uses for the actual translation and
skips the sentencizer. This is an integration choice, not a reimplementation of the model.

Protocol
--------
Request  (one JSON object per line):
    {"id": 1, "cmd": "ping"}
    {"id": 2, "cmd": "translate", "texts": ["...", "..."]}
    {"id": 3, "cmd": "shutdown"}

Response (one JSON object per line):
    {"id": 1, "ok": true, "pong": true, "package": "...", "version": "..."}
    {"id": 2, "ok": true, "translations": ["...", "..."]}
    {"id": 2, "ok": false, "error": "..."}

Every response is flushed immediately: the caller is blocked on it.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time

# Windows would otherwise pick the ANSI code page for stdout and mangle Chinese output.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Argos' own defaults, from argostranslate.settings.
MAX_BATCH_SIZE = 32
BEAM_SIZE = 4
LENGTH_PENALTY = 0.2

# A Marian/OpenNMT model is trained on short segments; beyond this we split on sentence
# boundaries ourselves so a long description cannot be silently truncated.
MAX_TOKENS_PER_SEGMENT = 400

SENTENCE_SPLIT = re.compile(r"(?<=[.!?])\s+(?=[A-Z0-9\"'(])")


class EngineError(Exception):
    """A failure the caller should see verbatim."""


class ArgosPackage:
    """One installed Argos package directory (metadata.json + model/ + sentencepiece.model)."""

    def __init__(self, package_path: str) -> None:
        import ctranslate2
        import sentencepiece as spm

        self.path = package_path
        metadata_path = os.path.join(package_path, "metadata.json")
        if not os.path.isfile(metadata_path):
            raise EngineError("语言包缺少 metadata.json：" + package_path)

        with open(metadata_path, "r", encoding="utf-8") as handle:
            self.metadata = json.load(handle)

        sp_model = os.path.join(package_path, "sentencepiece.model")
        if not os.path.isfile(sp_model):
            raise EngineError("语言包缺少 sentencepiece.model：" + package_path)

        model_dir = os.path.join(package_path, "model")
        if not os.path.isdir(model_dir):
            raise EngineError("语言包缺少 model 目录：" + package_path)

        self.target_prefix = self.metadata.get("target_prefix", "") or ""

        started = time.time()
        self.sentencepiece = spm.SentencePieceProcessor(model_file=sp_model)
        self.translator = ctranslate2.Translator(
            model_dir,
            device="cpu",
            inter_threads=1,
            intra_threads=0,
            compute_type="auto",
        )
        self.load_seconds = round(time.time() - started, 3)

    def describe(self) -> dict:
        return {
            "package": os.path.basename(self.path.rstrip("\\/")),
            "from_code": self.metadata.get("from_code"),
            "to_code": self.metadata.get("to_code"),
            "package_version": self.metadata.get("package_version"),
            "argos_version": self.metadata.get("argos_version"),
            "load_seconds": self.load_seconds,
        }

    def _segments(self, text: str) -> list[str]:
        """Splits one text into pieces small enough for the model, keeping sentence order."""
        if len(self.sentencepiece.encode(text)) <= MAX_TOKENS_PER_SEGMENT:
            return [text]

        pieces: list[str] = []
        for sentence in SENTENCE_SPLIT.split(text):
            if not sentence:
                continue
            if len(self.sentencepiece.encode(sentence)) <= MAX_TOKENS_PER_SEGMENT:
                pieces.append(sentence)
                continue

            # A single sentence longer than the budget: cut on character count so the model
            # still sees the whole text, one window at a time.
            window = MAX_TOKENS_PER_SEGMENT
            for start in range(0, len(sentence), window):
                pieces.append(sentence[start:start + window])

        return pieces or [text]

    def translate(self, text: str) -> str:
        if not text.strip():
            return text

        segments = self._segments(text)
        tokenized = [self.sentencepiece.encode(segment, out_type=str) for segment in segments]

        target_prefix = None
        if self.target_prefix:
            target_prefix = [[self.target_prefix]] * len(tokenized)

        batches = self.translator.translate_batch(
            tokenized,
            target_prefix=target_prefix,
            replace_unknowns=True,
            max_batch_size=MAX_BATCH_SIZE,
            batch_type="tokens",
            beam_size=BEAM_SIZE,
            num_hypotheses=1,
            length_penalty=LENGTH_PENALTY,
            return_scores=True,
        )

        decoded: list[str] = []
        for batch in batches:
            value = self.sentencepiece.decode_pieces(batch.hypotheses[0])
            # Same post-processing Argos applies after tokenizing.
            value = value.replace("\u2581", " ").replace("_", " ")
            if self.target_prefix and value.startswith(self.target_prefix):
                value = value[len(self.target_prefix):]
            if value.startswith(" "):
                value = value[1:]
            decoded.append(value)

        # Argos joins the decoded segments of one paragraph without a separator, because the
        # sentencepiece marker already carries the spacing.
        return "".join(decoded)


def respond(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main() -> int:
    parser = argparse.ArgumentParser(description="ModManager local translation worker")
    parser.add_argument("--package-dir", required=True, help="Installed Argos package directory")
    parser.add_argument("--src", default="en")
    parser.add_argument("--tgt", default="zh")
    args = parser.parse_args()

    try:
        package = ArgosPackage(args.package_dir)
    except Exception as error:  # Reported on stdout so the caller can show the real reason.
        respond({"id": 0, "ok": False, "fatal": True, "error": "%s: %s" % (type(error).__name__, error)})
        return 3

    info = package.describe()
    respond({"id": 0, "ok": True, "ready": True, **info})
    sys.stderr.write("worker ready: %s\n" % json.dumps(info, ensure_ascii=False))
    sys.stderr.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
        except json.JSONDecodeError as error:
            respond({"id": 0, "ok": False, "error": "无法解析请求：" + str(error)})
            continue

        request_id = request.get("id", 0)
        command = request.get("cmd")

        if command == "shutdown":
            respond({"id": request_id, "ok": True})
            return 0

        if command == "ping":
            respond({"id": request_id, "ok": True, "pong": True, **info})
            continue

        if command == "translate":
            texts = request.get("texts") or []
            try:
                started = time.time()
                translations = [package.translate(str(text)) for text in texts]
                respond({
                    "id": request_id,
                    "ok": True,
                    "translations": translations,
                    "seconds": round(time.time() - started, 3),
                })
            except Exception as error:
                respond({"id": request_id, "ok": False, "error": "%s: %s" % (type(error).__name__, error)})
            continue

        respond({"id": request_id, "ok": False, "error": "未知命令：" + str(command)})

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception as fatal:  # Never exit silently: the caller is waiting on a line.
        respond({"id": 0, "ok": False, "fatal": True, "error": "%s: %s" % (type(fatal).__name__, fatal)})
        sys.exit(4)
