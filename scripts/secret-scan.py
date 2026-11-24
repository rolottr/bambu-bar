#!/usr/bin/env python3
"""Scan tracked and untracked non-ignored files for secrets, private paths, and Keychain API sprawl."""
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Single-file SecItem* ownership: only Sources/BambuBar/Services/Keychain.swift may call SecItem* APIs.
KEYCHAIN_OWNERS = {"Sources/BambuBar/Services/Keychain.swift"}

# Known mock/test dummy access codes used in simulators and unit tests.
DUMMY_ACCESS_CODES = {
    "12345678",
    "87654321",
    "00000000",
    "11111111",
    "01234567",
    "99999999",
    "aaaaaaaa",
    "abcdefgh",
    "deadbeef",
    "cafebabe",
    "feedface",
    "testcode",
    "code1234",
    "1234abcd",
    "abcd1234",
}

PEM_PRIVATE_KEY_RE = re.compile(
    rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE" + rb" KEY-----"
)
POLAR_TOKEN_RE = re.compile(rb"\bpolar_" + rb"(?:oat|at)_[A-Za-z0-9_-]{8,}\b")
USER_HOME_PATH_RE = re.compile(rb"/" + rb"Users/[A-Za-z0-9._-]+/")
SECITEM_CALL_RE = re.compile(rb"\bSecItem(?:Add|CopyMatching|Update|Delete)\s*\(")
ACCESS_CODE_ASSIGN_RE = re.compile(
    rb"""(?i)(?:["']?(?:user_)?access_code["']?|\bACCESS_CODE\b)\s*[:=]\s*["']?([A-Za-z0-9]{8})["']?(?![A-Za-z0-9_])"""
)

SOURCE_AND_DOC_EXTS = {
    ".swift",
    ".cs",
    ".py",
    ".sh",
    ".zsh",
    ".bash",
    ".ps1",
    ".md",
    ".txt",
    ".json",
    ".yaml",
    ".yml",
    ".plist",
    ".entitlements",
    ".xcconfig",
    ".xcstrings",
    ".xml",
    ".html",
    ".ts",
    ".js",
    ".c",
    ".h",
    ".m",
    ".iss",
}


def is_text_candidate(path: Path) -> bool:
    if path.suffix.lower() in SOURCE_AND_DOC_EXTS or path.name in {"Dockerfile", "Makefile", "LICENSE"}:
        return True
    return False


def main() -> int:
    raw_paths = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        cwd=ROOT,
    ).split(b"\0")

    files = sorted({p.decode("utf-8", errors="surrogateescape") for p in raw_paths if p})
    issues: list[tuple[str, str]] = []

    for relative in files:
        path = ROOT / relative
        if not path.is_file():
            continue

        if relative.startswith((".local/", "evidence/private/", "evidence/live/")):
            issues.append((relative, "private local/capture artifact tracked"))

        if path.name == "BambuStudio.conf":
            issues.append((relative, "BambuStudio.conf should never be committed (may contain printer access codes)"))

        data = path.read_bytes()

        if PEM_PRIVATE_KEY_RE.search(data):
            issues.append((relative, "PEM private key"))

        if POLAR_TOKEN_RE.search(data):
            issues.append((relative, "Polar API token (polar_oat_/polar_at_)"))

        for match in ACCESS_CODE_ASSIGN_RE.finditer(data):
            code = match.group(1).decode("ascii", errors="ignore")
            if code.lower() not in DUMMY_ACCESS_CODES:
                issues.append((relative, "possible real printer access code"))
                break

        if is_text_candidate(path) and b"\x00" not in data[:8192]:
            if USER_HOME_PATH_RE.search(data):
                issues.append((relative, "hardcoded /Users/<name>/ path in tracked source or docs"))

        if relative.endswith(".swift") and relative not in KEYCHAIN_OWNERS:
            if SECITEM_CALL_RE.search(data):
                issues.append(
                    (
                        relative,
                        "SecItem* call outside sole Keychain owner (Sources/BambuBar/Services/Keychain.swift)",
                    )
                )

    for rel, reason in issues:
        print(f"FAIL {rel}: {reason}")

    print(
        json.dumps(
            {
                "files_reviewed": len(files),
                "issues": len(issues),
                "secret_values_printed": False,
            }
        )
    )
    return 1 if issues else 0


if __name__ == "__main__":
    raise SystemExit(main())
