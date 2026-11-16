#!/usr/bin/env python3
"""aisdlc — read an agent-SDLC role's protocol, recall the record, edit a prompt.

WHAT THIS IS. The CLI for the agent-SDLC mode whose contract is `_aisdlc/README.md`. It reads a
role's operating protocol (a *mental model*'s generated CONTENT) from the memory bank named in
`_aisdlc/config.yaml`, recalls the team's precedent from the same bank, replaces the PROMPT a
protocol regenerates from, and seeds the whole model set from the tracked files.

Dependency-free on purpose: python3 standard library only. No vendor SDK, no sibling-repository
checkout by relative path, no YAML library (the config is two keys, read line-wise below). The
only thing it links against is the store's own HTTP API.

THE FOUR CASES IT MUST NEVER CONFLATE (`_aisdlc/README.md`, "Honest absence"):

  * **unreachable** — the store could not be asked. Exit 3. An outage.
  * **absent** — no model by that name. Exit 4. A normal condition.
  * **still generating** — the model exists but its content is empty because the server has not
    generated it yet. Exit 4 as well, because the caller's fallback is the same (run on the static
    core alone) — but the MESSAGE must say "still generating" and give `last_refreshed_at`, where
    the absent message says "no model by that name". An empty document is NEVER printed as content.
  * **silent** — a recall that simply found nothing relevant. Exit 0, and say the record was
    silent. Never dressed up as an outage, and never filled with invented precedent.

The 3-vs-4 split is the load-bearing one: a missing model reported as an outage (or an outage
reported as a missing model) is the exact failure this tool exists to prevent.

EXIT CODES

  0  ok
  2  bad usage — a malformed command line, a `_aisdlc/config.yaml` that does not name a bank and a
     store URL, or a role directory that does not name a model
  3  store unreachable — connection refused, timed out, not HTTP, or a body that is not the JSON
     the store documents
  4  model absent (message says which of "absent" / "still generating")
  5  refused — `set-prompt` naming a model that does not exist, without `--force`
  6  the store was reached and answered with an HTTP error status, or with a body that could not be
     used. Deliberately NOT 3: the store answered, so calling that "unreachable" would be the lie
     this tool exists to prevent.

THE BANK PREFLIGHT (`check`, and the first step of `seed`). A bank is not a model. The store answers
404 `Bank '<id>' not found` for every model read in a bank nobody created, and each role then works
from its static core alone while reporting a refusal that reads like an infrastructure fault. So
this tool asks the store's own bank collection before it writes anything, and `check` answers the
three questions a revision must not start without: the configured bank exists, every model named in
`_aisdlc/<role>/MODEL.md` is present, and each carries content. The bank in the refusal is ALWAYS
the configured one — no name is written into this file, and a scaffolded deployment that names a
different bank is checked against that bank. The bank's absence is exit 6, never 3: the store
answered.

WHERE THE BANK COMES FROM. Exactly one place may name a bank: `_aisdlc/config.yaml`, plus the
explicit `--bank <name>` / `$AISDLC_BANK` override (and `--url` / `$AISDLC_HINDSIGHT_URL` for the
address). No bank name is written into this file, and none ever may be.

WHAT IT DELIBERATELY DOES NOT DO. It does not write a model's content (the server generates it and
would overwrite any edit); it does not schedule refreshes (the server owns regeneration; the
tracked trigger does carry a cron, but the cron is the server's to honour); and it hardcodes no
role and no model name — `seed` discovers this project's roles from the tracked tree.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# ---------------------------------------------------------------------------------------------
# Constants. Every one of them is either a protocol fact from the store's own OpenAPI document,
# or a filesystem fact about this repository. A bank name is not among them, and cannot be.

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_UNREACHABLE = 3
EXIT_ABSENT = 4
EXIT_REFUSED = 5
EXIT_STORE_ERROR = 6

TOOL_DIR = Path(__file__).resolve().parent          # _aisdlc/tool/
AISDLC_DIR = TOOL_DIR.parent                         # _aisdlc/
REPO_DIR = AISDLC_DIR.parent                         # the repository root
CONFIG_FILE = AISDLC_DIR / "config.yaml"
CACHE_DIR = TOOL_DIR / ".aisdlc-cache"               # gitignored; see _aisdlc/tool/.gitignore

# The generated content is opt-in: the store's read surface defaults to `detail=metadata`, in
# which `content` and `source_query` both come back EMPTY WITH NO ERROR. Every content-carrying
# read here asks for `detail=content` explicitly; a hand-rolled curl must do the same.
DETAIL_CONTENT = "content"
DETAIL_METADATA = "metadata"

# How long to wait. The mental-model surfaces answer in milliseconds — a short timeout is what
# makes "unreachable" fail fast and distinct. `recall` is the exception: it runs an LLM retrieval
# over the bank and was MEASURED at 125s for one query against this deployment's store, so its
# default is minutes, not seconds, and a timeout there is still reported as unreachable (never as
# a silent record).
TIMEOUT_META = 30
TIMEOUT_RECALL = 300

# What `seed` creates with. The trigger is the deployment's own configuration (full regeneration
# daily at 10:00 UTC, refresh tracing on — `_aisdlc/config.yaml` explains why), and the token
# budget matches the models this bank already carries, so a bank rebuilt from the tracked files on
# a fresh machine is the bank this one is.
SEED_TRIGGER = {"mode": "full", "refresh_cron": "0 10 * * *", "keep_trace": True}
SEED_MAX_TOKENS = 4096

# The budget levels the store accepts, and the fact types a recall may be scoped to. Validated
# here so a typo is exit 2 before a two-minute recall runs, rather than a 422 afterwards.
BUDGETS = ("low", "mid", "high")
FACT_TYPES = ("world", "experience", "observation")

DEFAULT_RECALL_LIMIT = 10      # results printed; 0 means all. The store returns up to `max_tokens`.
DEFAULT_RECALL_MAX_TOKENS = 4096


# ---------------------------------------------------------------------------------------------
# Failures. Three distinct exception types on purpose: the caller decides the exit code, but the
# type records WHICH thing happened so no code path can conflate two of them.

class UsageError(Exception):
    """A malformed command line, or a deployment/tracked-set defect. Exit 2."""


class Unreachable(Exception):
    """The store could not be asked. Exit 3."""


class StoreError(Exception):
    """The store answered, with an error status or an unusable body. Exit 6."""


class BankMissing(StoreError):
    """The configured bank is not on the store, so there is nothing there to read or seed into.

    A StoreError and not an Unreachable: the store was asked and it answered. Its own message is one
    line naming the bank, what the store does hold, and the request that creates it — the whole point
    of the class is that this case cannot be reported as an outage, and cannot be reported as a
    paragraph about an HTTP URL either.
    """


# ---------------------------------------------------------------------------------------------
# Configuration — `_aisdlc/config.yaml`, read line-wise because it is two keys and a YAML
# dependency would be the only third-party import in the repository's tooling.

def parse_config(path: Path) -> dict:
    """Two keys out of a comment-heavy file, without a YAML library.

    Lines that are blank or start with `#` are skipped; the rest are `key: value`. A value may be
    quoted, and an unquoted value may carry a trailing `# comment`. Anything else raises, so a
    config file this cannot read is reported rather than half-read."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise UsageError(
            f"cannot read {path} ({exc.strerror or exc}). This file names the bank and the store "
            f"this tool reads; without it there is nowhere to point."
        ) from exc
    out: dict[str, str] = {}
    for lineno, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if ":" not in line:
            raise UsageError(f"{path}:{lineno}: not a `key: value` line: {raw!r}")
        key, value = line.split(":", 1)
        key = key.strip()
        value = value.strip()
        if value[:1] in ('"', "'") and value[-1:] == value[:1] and len(value) >= 2:
            value = value[1:-1]
        else:
            value = re.sub(r"\s+#.*$", "", value).strip()
        out[key] = value
    return out


class Config:
    """Where to read: the resolved bank and store address, and where each came from."""

    def __init__(self, bank: str, url: str, bank_src: str, url_src: str):
        self.bank = bank
        self.url = url.rstrip("/")
        self.bank_src = bank_src
        self.url_src = url_src

    def __str__(self) -> str:
        return f"bank {self.bank!r} ({self.bank_src}) at {self.url} ({self.url_src})"

    def models_collection_url(self) -> str:
        # The bank id is path-quoted: a configured name with a space or a slash must not be able
        # to change WHICH resource is addressed.
        bank = urllib.parse.quote(self.bank, safe="")
        return f"{self.url}/v1/default/banks/{bank}/mental-models"

    def banks_url(self) -> str:
        """The store's bank collection — the only surface that answers "does this bank exist?"."""
        return f"{self.url}/v1/default/banks"

    def models_url(self, detail: str) -> str:
        return f"{self.models_collection_url()}?detail={detail}&limit=1000"

    def model_url(self, model_id: str, sub: str = "") -> str:
        bank = urllib.parse.quote(self.bank, safe="")
        mid = urllib.parse.quote(model_id, safe="")
        return f"{self.url}/v1/default/banks/{bank}/mental-models/{mid}{sub}"

    def recall_url(self) -> str:
        bank = urllib.parse.quote(self.bank, safe="")
        return f"{self.url}/v1/default/banks/{bank}/memories/recall"


def resolve_config(args) -> Config:
    """`--flag` beats `$ENV` beats `_aisdlc/config.yaml`. There is no fourth place."""
    bank = getattr(args, "bank", None) or os.environ.get("AISDLC_BANK") or None
    url = getattr(args, "url", None) or os.environ.get("AISDLC_HINDSIGHT_URL") or None
    bank_src = "argv" if getattr(args, "bank", None) else (
        "env (AISDLC_BANK)" if os.environ.get("AISDLC_BANK") else None)
    url_src = "argv" if getattr(args, "url", None) else (
        "env (AISDLC_HINDSIGHT_URL)" if os.environ.get("AISDLC_HINDSIGHT_URL") else None)
    if bank is None or url is None:
        cfg = parse_config(CONFIG_FILE)
        if bank is None:
            bank = cfg.get("bank") or None
            bank_src = f"{CONFIG_FILE.name}: bank"
        if url is None:
            url = cfg.get("hindsight_url") or None
            url_src = f"{CONFIG_FILE.name}: hindsight_url"
    if not bank:
        raise UsageError(
            f"no bank: set `bank` in {CONFIG_FILE}, or pass --bank / $AISDLC_BANK"
        )
    if not url:
        raise UsageError(
            f"no store address: set `hindsight_url` in {CONFIG_FILE}, or pass --url / "
            f"$AISDLC_HINDSIGHT_URL"
        )
    return Config(bank, url, bank_src, url_src)


# ---------------------------------------------------------------------------------------------
# HTTP — the store's documented surface, and nothing else.

def http_json(method: str, url: str, body: dict | None = None, timeout: int = TIMEOUT_META):
    """One JSON request.

    Raises Unreachable when the store could not be asked (connection refused, timeout, not HTTP, or
    a body that is not JSON — that is not the memory store the address claims to be); StoreError
    when the store answered with an error status or a body that carries no answer."""
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            detail = exc.read().decode("utf-8", "replace").strip()[:400]
        except Exception:  # pragma: no cover - the body is a courtesy, not the answer
            pass
        raise StoreError(
            f"the store answered HTTP {exc.code} {exc.reason} for {method} {url}"
            + (f" — {detail}" if detail else "")
        ) from exc
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        raise Unreachable(f"could not reach the store at {url}: {reason}") from exc
    except (TimeoutError, OSError) as exc:
        raise Unreachable(f"could not reach the store at {url}: {exc}") from exc
    if not raw.strip():
        raise Unreachable(f"the store at {url} answered an empty body")
    try:
        return json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise Unreachable(
            f"the store at {url} answered {len(raw)} bytes that are not JSON ({exc}) — not the "
            f"memory store the configured address claims to be"
        ) from exc


def bank_ids(cfg: Config) -> list[str]:
    """Every bank id the store holds, as the store's own collection reports them."""
    payload = http_json("GET", cfg.banks_url())
    banks = payload.get("banks")
    if not isinstance(banks, list):
        raise StoreError(
            f"the store's bank list carried no `banks` array "
            f"(keys: {sorted(payload) if isinstance(payload, dict) else type(payload).__name__})"
        )
    return [str(b.get("bank_id")) for b in banks if isinstance(b, dict) and b.get("bank_id")]


def bank_present(cfg: Config) -> bool:
    """Is the CONFIGURED bank on the store? The collection is asked, never the bank's own path.

    GET on a bank id is not an existence test — the store answers 405 there, and a 404 would say
    nothing about whether the bank exists. The collection is the one surface that answers it, and it
    is asked about the name the configuration resolves to, so a scaffolded deployment is checked
    against ITS bank and not this one.
    """
    return cfg.bank in bank_ids(cfg)


def bank_missing_line(cfg: Config) -> str:
    """ONE line: which bank is missing, what the store does hold, and the request that creates it."""
    try:
        held = ", ".join(bank_ids(cfg)) or "no banks at all"
    except (Unreachable, StoreError):
        held = "the bank list could not be read"
    path = f"{cfg.url}/v1/default/banks/{urllib.parse.quote(cfg.bank, safe='')}"
    return (
        f"bank {cfg.bank!r} does not exist on the store at {cfg.url} (it holds: {held}) — nothing has "
        f"created it, so no role can read its protocol and no model can be seeded into it. Create it "
        f"with the store's own API: PUT {path} "
        f"{json.dumps({'name': cfg.bank, 'mission': '...'})} — then run `aisdlc seed`."
    )


def require_bank(cfg: Config) -> None:
    """Stop, before anything is written, when the configured bank is not on the store.

    A model cannot be created into a bank nobody created: the store answers 404 and the run would end
    in a URL and a JSON body that read like an infrastructure fault. This asks first and refuses with
    one line naming the bank. It creates nothing — a bank is the operator's decision (one bank per
    project, and a bank that already exists is left alone), so the tool's writes stay what they were:
    a model's prompt, and nothing else.
    """
    if not bank_present(cfg):
        raise BankMissing(bank_missing_line(cfg))


def list_models(cfg: Config, detail: str) -> list[dict]:
    """Every mental model in the bank, at the requested detail level."""
    try:
        payload = http_json("GET", cfg.models_url(detail))
    except StoreError:
        # The store ANSWERED and refused. The refusal that matters here is a bank nobody created —
        # 404, body `Bank '<id>' not found` — and it is settled by asking the collection rather than
        # by matching the body's prose. Any other refusal is re-raised unchanged, with its own status.
        try:
            present = bank_present(cfg)
        except (Unreachable, StoreError):
            present = True                      # cannot tell; report the original refusal untouched
        if not present:
            raise BankMissing(bank_missing_line(cfg)) from None
        raise
    items = payload.get("items")
    if not isinstance(items, list):
        raise StoreError(
            f"the store's model list for bank {cfg.bank!r} carried no `items` array "
            f"(keys: {sorted(payload) if isinstance(payload, dict) else type(payload).__name__})"
        )
    return items


def find_model(items: list[dict], name: str) -> dict | None:
    """There is no get-by-name endpoint: name resolution is a list plus a match.

    Ids are unique, names are not — take the FIRST ordinal match, and warn on stderr if the bank
    happens to carry more than one, so a duplicate is visible rather than silently picked."""
    matches = [m for m in items if m.get("name") == name]
    if len(matches) > 1:
        warn(f"{len(matches)} models in this bank are named {name!r}; using the first "
             f"({matches[0].get('id')})")
    return matches[0] if matches else None


def model_id_of(m: dict) -> str:
    mid = m.get("id")
    if not mid:
        raise StoreError(f"a model named {m.get('name')!r} in this bank carries no id")
    return mid


def split_content(m: dict) -> tuple[bool, str]:
    """`(True, "")` when the content is empty, else `(False, content)`.

    Empty is 'still generating', never 'no content'."""
    content = m.get("content")
    if not isinstance(content, str) or not content.strip():
        return True, ""
    return False, content


def refreshed_at(m: dict) -> str:
    return m.get("last_refreshed_at") or "never refreshed"


# ---------------------------------------------------------------------------------------------
# The cache. Keyed on the model name, storing the server's `last_refreshed_at` plus a content
# hash, with the content itself under a hash-named file — so a server-side regeneration cannot
# serve yesterday's protocol as today's: the fresh read writes new metadata pointing at a new
# content file, and the old content is simply orphaned.

def cache_meta_path(model: str) -> Path:
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", model)
    return CACHE_DIR / "meta" / f"{safe}.json"


def cache_content_path(digest: str) -> Path:
    return CACHE_DIR / "content" / f"{digest}.md"


def read_cache(model: str, live_refreshed_at: str | None) -> str | None:
    """The cached content, or None. A hit requires the metadata file, a `last_refreshed_at` that
    equals what the server reports RIGHT NOW, and a content file whose bytes still hash to what
    the metadata recorded."""
    path = cache_meta_path(model)
    try:
        meta = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(meta, dict):
        return None
    if live_refreshed_at is None or meta.get("last_refreshed_at") != live_refreshed_at:
        return None
    digest = meta.get("content_sha256")
    if not isinstance(digest, str):
        return None
    cpath = cache_content_path(digest)
    try:
        blob = cpath.read_bytes()
    except OSError:
        return None
    if hashlib.sha256(blob).hexdigest() != digest:
        return None
    return blob.decode("utf-8")


def write_cache(model: str, live_refreshed_at: str | None, content: str) -> None:
    """Write metadata last, so a crash can only ever leave an orphan, never a pointer at nothing."""
    blob = content.encode("utf-8")
    digest = hashlib.sha256(blob).hexdigest()
    cpath = cache_content_path(digest)
    cpath.parent.mkdir(parents=True, exist_ok=True)
    tmp = cpath.with_suffix(".tmp")
    tmp.write_bytes(blob)
    os.replace(tmp, cpath)
    meta = {
        "model": model,
        "last_refreshed_at": live_refreshed_at,
        "content_sha256": digest,
        "content_bytes": len(blob),
        "cached_at": now_iso(),
    }
    mpath = cache_meta_path(model)
    mpath.parent.mkdir(parents=True, exist_ok=True)
    tmp = mpath.with_suffix(".tmp")
    tmp.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, mpath)


def now_iso() -> str:
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).isoformat()


# ---------------------------------------------------------------------------------------------
# Prompt files. The tracked `PROMPT.md` is the model's `source_query`; its leading HTML comment is
# a note to whoever maintains the folder (it says what the file is and how seeding uses it), not
# part of the instruction the server runs. It is therefore stripped before the prompt is compared
# or sent, for three reasons: the store already carries the stripped form, so stripping is what
# makes seed idempotent; a comment about banks and memory inside a prompt would put the
# mechanism's meta-commentary into a document whose own output rules forbid exactly that; and a
# prompt that says "seeding reads THIS file" is meaningless to the model that consumes it.

_LEADING_COMMENT = re.compile(r"\A\s*<!--.*?-->\s*", re.S)


def prompt_body(path: Path) -> str:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise UsageError(f"cannot read {path} ({exc.strerror or exc})") from exc
    if not text.strip():
        raise UsageError(f"{path} is empty — refusing to write an empty prompt")
    return _LEADING_COMMENT.sub("", text, count=1).rstrip()


def discover_roles() -> list[tuple[str, str, Path, Path]]:
    """This project's roles, discovered from the tracked tree — never hardcoded.

    A role is a directory under `_aisdlc/` holding both `MODEL.md` (which names the model) and
    `PROMPT.md` (which is its prompt). `MODEL.md` is the authority on the name: the mode's rule is
    that a role's `MODEL.md` names which model and never which bank, so `seed` reads the name
    there rather than carrying a second, driftable table of its own."""
    roles = []
    for d in sorted(p for p in AISDLC_DIR.iterdir() if p.is_dir()):
        model_md = d / "MODEL.md"
        prompt_md = d / "PROMPT.md"
        if not (model_md.is_file() and prompt_md.is_file()):
            continue
        named = re.search(r"^\|\s*\*\*Model\*\*\s*\|\s*`?([A-Za-z0-9._-]+)`?\s*\|",
                          model_md.read_text(encoding="utf-8"), re.M)
        if not named:
            raise UsageError(
                f"{model_md} names no model: expected a table row `| **Model** | `name` |`"
            )
        roles.append((d.name, named.group(1), model_md, prompt_md))
    if not roles:
        raise UsageError(
            f"no role directories found under {AISDLC_DIR}: expected <role>/MODEL.md beside "
            f"<role>/PROMPT.md"
        )
    return roles


# ---------------------------------------------------------------------------------------------
# Output helpers. stdout carries the answer and nothing else, so a protocol read can be piped;
# every diagnostic goes to stderr.

def warn(msg: str) -> None:
    print(f"aisdlc: {msg}", file=sys.stderr)


def emit(text: str) -> None:
    sys.stdout.write(text if text.endswith("\n") else text + "\n")


def write_out(path: str, text: str, what: str) -> None:
    try:
        Path(path).write_text(text if text.endswith("\n") else text + "\n", encoding="utf-8")
    except OSError as exc:
        raise UsageError(f"cannot write {path} ({exc.strerror or exc})") from exc
    emit(f"wrote {what} to {path} ({len(text.encode('utf-8'))} bytes)")


# ---------------------------------------------------------------------------------------------
# Commands.

def cmd_list(cfg: Config, args) -> int:
    """What is in the bank. Content is requested so 'still generating' can be reported truthfully."""
    items = list_models(cfg, DETAIL_CONTENT)
    print(f"{cfg}")
    if not items:
        print("  (the bank holds no mental models at all — nothing to list)")
        return EXIT_OK
    wid = max(len(str(m.get("name") or "")) for m in items)
    for m in items:
        generating, content = split_content(m)
        detail = "still generating (content empty)" if generating else f"{len(content.encode('utf-8'))} chars"
        print(f"  {str(m.get('name') or '').ljust(wid)}  {str(m.get('id') or '-'):38}  "
              f"{refreshed_at(m)}  {detail}")
    print(f"  {len(items)} model(s) in bank {cfg.bank!r}")
    return EXIT_OK


def cmd_read(cfg: Config, args) -> int:
    """A model's generated CONTENT. Three outcomes, three messages, never conflated."""
    model = args.model
    if not args.no_cache:
        # Phase 1, cheap: the metadata surface carries `last_refreshed_at` and no content. It also
        # settles absent vs unreachable vs still-generating before any content is fetched.
        items = list_models(cfg, DETAIL_METADATA)
        found = find_model(items, model)
        if found is None:
            return report_absent(cfg, model)
        cached = read_cache(model, found.get("last_refreshed_at"))
        if cached is not None:
            warn(f"{model}: served from the local cache (last refreshed "
                 f"{refreshed_at(found)}) — --no-cache for a live read")
            if args.out:
                write_out(args.out, cached, model)
            else:
                emit(cached)
            return EXIT_OK
    # Phase 2: the live content, explicitly requested. Also the whole path when --no-cache.
    items = list_models(cfg, DETAIL_CONTENT)
    found = find_model(items, model)
    if found is None:
        return report_absent(cfg, model)
    generating, content = split_content(found)
    if generating:
        print(f"aisdlc: model {model!r} EXISTS in {cfg} but its content is EMPTY — the server is "
              f"STILL GENERATING it (last refreshed {refreshed_at(found)}).\n"
              f"        This is not a missing model, and it is not a protocol: run on the static "
              f"core alone and say the model is still generating.", file=sys.stderr)
        return EXIT_ABSENT
    if not args.no_cache:
        write_cache(model, found.get("last_refreshed_at"), content)
    warn(f"{model}: {len(content.encode('utf-8'))} bytes, last refreshed {refreshed_at(found)}")
    if args.out:
        write_out(args.out, content, model)
    else:
        emit(content)
    return EXIT_OK


def report_absent(cfg: Config, model: str) -> int:
    print(f"aisdlc: no mental model named {model!r} in {cfg}. It is ABSENT — the store was asked "
          f"and answered. Run on the static core alone and say the protocol was missing.",
          file=sys.stderr)
    return EXIT_ABSENT


def cmd_check(cfg: Config, args) -> int:
    """THE PREFLIGHT — the three facts a revision must not start without.

    This is asked at boot by whoever launches a role, and asked once here: does the bank this
    deployment names exist on the store, is every model named in `_aisdlc/<role>/MODEL.md` present,
    and does each carry content? It writes nothing and creates nothing.

    Each model's answer carries the FIRST LINE of its content, so "has content" is something a reader
    can see is a protocol rather than bytes. A model the store refuses to generate is a named state:
    EMPTY, with its refresh time — never a pass, and never a blank document printed as protocol.

    Exit 0 all three hold · 3 the store could not be asked · 4 a declared model is absent or its
    content is empty · 6 the configured bank does not exist (or another store refusal). The bank's
    absence is 6, not 3: the store ANSWERED.
    """
    items = list_models(cfg, DETAIL_CONTENT)          # BankMissing names the bank; Unreachable is 3
    roles = discover_roles()
    print(f"check {cfg}")
    print(f"  ok    the bank exists and holds {len(items)} mental model(s)")
    failures: list[str] = []
    for role, name, _model_md, _prompt_md in roles:
        found = find_model(items, name)
        declared_by = f"_aisdlc/{role}/MODEL.md"
        if found is None:
            failures.append(f"{name}: ABSENT (declared by {declared_by}, and the bank holds no model "
                            f"by that name)")
            print(f"  FAIL  {name:38}  absent")
            continue
        empty, content = split_content(found)
        if empty:
            failures.append(f"{name}: EMPTY (present, no content — still generating, or generation "
                            f"failed; last refreshed {refreshed_at(found)})")
            print(f"  FAIL  {name:38}  empty (last refreshed {refreshed_at(found)})")
            continue
        size = len(content.encode("utf-8"))
        print(f"  ok    {name:38}  {size:>6} B  refreshed {refreshed_at(found)}")
        first = content.strip().splitlines()[0].strip()
        print(f"        {first[:104]}{' …' if len(first) > 104 else ''}")
    if failures:
        print("")
        for f in failures:
            print(f"  FAIL  {f}")
        print(f"check: FAILED — {len(failures)} of {len(roles)} declared model(s) carry no protocol in "
              f"bank {cfg.bank!r}")
        return EXIT_ABSENT
    print(f"check: PASS — bank {cfg.bank!r} exists and all {len(roles)} declared model(s) carry content")
    return EXIT_OK


def cmd_recall(cfg: Config, args) -> int:
    """Precedent: what the organisation has already learned about the decision at hand."""
    types = None
    if args.types:
        types = [t.strip() for t in args.types.split(",") if t.strip()]
        bad = [t for t in types if t not in FACT_TYPES]
        if bad:
            raise UsageError(f"--types accepts only {', '.join(FACT_TYPES)} (got {', '.join(bad)})")
    budget = args.budget or "mid"
    if budget not in BUDGETS:
        raise UsageError(f"--budget accepts only {', '.join(BUDGETS)} (got {budget!r})")
    body = {"query": args.query, "budget": budget, "max_tokens": args.max_tokens}
    if types:
        body["types"] = types
    payload = http_json("POST", cfg.recall_url(), body, timeout=args.timeout)
    results = payload.get("results")
    if not isinstance(results, list):
        raise StoreError(
            f"the recall response carried no `results` array "
            f"(keys: {sorted(payload) if isinstance(payload, dict) else type(payload).__name__})"
        )
    limit = args.limit
    shown = results if limit == 0 else results[:limit]
    lines = []
    if not shown:
        # The fourth case, and a normal one. Never an outage, never invented precedent.
        lines.append(f"recall: the record is SILENT on this — the bank {cfg.bank!r} holds nothing "
                     f"relevant to the query. Nothing was found; proceed, and say the record was "
                     f"silent.")
    else:
        lines.append(f"recall: {len(results)} result(s) from {cfg} for {args.query!r}"
                     + (f" — showing {len(shown)}" if len(shown) != len(results) else ""))
        for i, r in enumerate(shown, 1):
            text = (r.get("text") or "").strip()
            if len(text) > 1200:
                text = text[:1200] + " … (truncated)"
            lines.append(f"\n[{i}] {r.get('type') or 'untyped'}"
                         f"{'  ' + r['occurred_start'] if r.get('occurred_start') else ''}\n{text}")
        if len(shown) != len(results):
            lines.append(f"\nrecall: {len(results) - len(shown)} further result(s) not shown "
                         f"(--limit {len(results)} for all)")
    out = "\n".join(lines)
    if args.out:
        write_out(args.out, out, "recall results")
    else:
        emit(out)
    return EXIT_OK


def put_prompt(cfg: Config, model: str, found: dict | None, prompt: str, args) -> int:
    """Replace a model's prompt (`source_query`). The only write this tool makes to a model,
    because the content is generated and the next refresh would overwrite anything written there."""
    if found is None:
        if not args.force:
            print(f"aisdlc: REFUSED — no model named {model!r} exists in {cfg}, so there is "
                  f"nothing to edit. Nothing was created: a typo in a model name must not silently "
                  f"create a second, empty protocol. Pass --force to create it here with the "
                  f"tracked trigger and the prompt from {args.file}.", file=sys.stderr)
            return EXIT_REFUSED
        warn(f"{model}: absent — creating it (--force)")
        http_json("POST", cfg.models_collection_url(), {
            "name": model,
            "source_query": prompt,
            "max_tokens": SEED_MAX_TOKENS,
            "trigger": dict(SEED_TRIGGER),
        })
        emit(f"created {model} in {cfg} — the server generates its content from the new prompt")
        return EXIT_OK
    if (found.get("source_query") or "").rstrip() == prompt:
        emit(f"{model}: prompt already equal to {args.file} — changed nothing")
        return EXIT_OK
    http_json("PATCH", cfg.model_url(model_id_of(found)), {"source_query": prompt})
    emit(f"{model}: prompt replaced in {cfg}. The content regenerates from the new prompt on the "
         f"next refresh (`aisdlc refresh -m {model}` regenerates it now); the current content was "
         f"generated from the OLD prompt until then.")
    return EXIT_OK


def cmd_set_prompt(cfg: Config, args) -> int:
    prompt = prompt_body(Path(args.file))
    items = list_models(cfg, DETAIL_CONTENT)
    return put_prompt(cfg, args.model, find_model(items, args.model), prompt, args)


def cmd_refresh(cfg: Config, args) -> int:
    """An explicit refresh. The store runs these immediately (its `min_refresh_interval_seconds`
    floor applies to the scheduled triggers, not to an explicit ask), and the content is rewritten
    in the background — so this returns as soon as the store has accepted it, and a `read` right
    after may still say 'still generating'."""
    items = list_models(cfg, DETAIL_METADATA)
    found = find_model(items, args.model)
    if found is None:
        return report_absent(cfg, args.model)
    payload = http_json("POST", cfg.model_url(model_id_of(found), "/refresh"))
    emit(f"refresh: {args.model} submitted to {cfg} "
         f"(status={payload.get('status')}, operation_id={payload.get('operation_id')}). "
         f"The store regenerates the content in the background; `read` reports it as still "
         f"generating until it lands.")
    return EXIT_OK


def cmd_seed(cfg: Config, args) -> int:
    """Create or update this project's role models from the tracked `PROMPT.md` files.

    This is the step that makes the mode survive a wiped volume: the models live in the memory
    store's own storage, the prompts live in this repository, and seeding reads the tracked file
    to create the model when it is absent and to update the prompt when it differs. Idempotent —
    a second run on an unchanged bank creates nothing, patches nothing and says so."""
    roles = discover_roles()
    # THE BANK FIRST, BEFORE A SINGLE MODEL. Creating a model into a bank nobody created answers 404,
    # and the run then ends in a URL and a JSON body that read like an outage — which is how a fresh
    # install came to seed nothing, quietly, and start revisions against an absent bank.
    require_bank(cfg)
    items = list_models(cfg, DETAIL_CONTENT)
    created = updated = unchanged = 0
    print(f"seed: {len(roles)} role(s) discovered under {AISDLC_DIR}, into {cfg}")
    for role, model, _model_md, prompt_md in roles:
        prompt = prompt_body(prompt_md)
        found = find_model(items, model)
        rel = prompt_md.relative_to(REPO_DIR)
        if found is None:
            http_json("POST", cfg.models_collection_url(), {
                "name": model,
                "source_query": prompt,
                "max_tokens": SEED_MAX_TOKENS,
                "trigger": dict(SEED_TRIGGER),
            })
            created += 1
            print(f"  created   {model:38} ({role}) from {rel}")
            warn(f"{model}: created — the server generates the content from the prompt; until it "
                 f"lands, a read reports the model as still generating")
        elif (found.get("source_query") or "").rstrip() != prompt:
            http_json("PATCH", cfg.model_url(model_id_of(found)), {"source_query": prompt})
            updated += 1
            print(f"  updated   {model:38} ({role}) prompt replaced from {rel}")
        else:
            unchanged += 1
            print(f"  unchanged {model:38} ({role}) present, prompt equal to {rel}")
    print(f"seed: {created} created, {updated} updated, {unchanged} unchanged "
          f"({len(roles)} role model(s) present with the tracked prompts)")
    return EXIT_OK


def cmd_cache(cfg: Config, args) -> int:
    if args.clear:
        try:
            shutil.rmtree(CACHE_DIR)
        except FileNotFoundError:
            pass
        emit(f"cache cleared: {CACHE_DIR} is gone (it is rebuilt on the next read)")
        return EXIT_OK
    metas = sorted((CACHE_DIR / "meta").glob("*.json")) if (CACHE_DIR / "meta").is_dir() else []
    contents = sorted((CACHE_DIR / "content").glob("*.md")) if (CACHE_DIR / "content").is_dir() else []
    print(f"cache {CACHE_DIR}")
    if not metas:
        print("  (empty — the next successful read fills it)")
    for p in metas:
        try:
            meta = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            print(f"  {p.name:40} unreadable — ignored")
            continue
        print(f"  {meta.get('model') or p.stem:40} refreshed {meta.get('last_refreshed_at')} "
              f"hash {str(meta.get('content_sha256'))[:12]} {meta.get('content_bytes')} bytes")
    print(f"  {len(contents)} content file(s) — orphans are harmless; --clear empties the whole cache")
    return EXIT_OK


# ---------------------------------------------------------------------------------------------
# Command line.

def add_globals(p: argparse.ArgumentParser) -> None:
    """`--bank` / `--url` / `--timeout` are accepted before OR after the subcommand. On the
    subcommand copies the default is SUPPRESS, so a subparser that was not given the flag leaves
    the value the top-level parser already parsed alone instead of resetting it to None."""
    g = p.add_argument_group("configuration (overrides _aisdlc/config.yaml)")
    g.add_argument("--bank", metavar="NAME", dest="bank",
                   help="the bank to read; default: `bank` in _aisdlc/config.yaml, or "
                        "$AISDLC_BANK")
    g.add_argument("--url", metavar="URL", dest="url",
                   help="the store's base URL; default: `hindsight_url` in _aisdlc/config.yaml, "
                        "or $AISDLC_HINDSIGHT_URL")
    g.add_argument("--timeout", metavar="SECONDS", type=int, dest="timeout",
                   help="per-request timeout in seconds (default 30; a recall's default is "
                        "300 - it runs a retrieval over the bank)")


def cmd_sync(cfg: Config, args) -> int:
    """Pull every declared mental model to disk, and prove the learning loop is alive.

    The store is the authority for content; this command is what gives the roles a durable copy and
    what makes "the loop is working" checkable instead of assumed. Two questions, two modes:

      sync            fetch every declared model's content, write it to the local cache, print a row
                      per model (bytes, content hash, last refreshed). A model whose content is empty
                      is reported as EMPTY and does not pass --check.
      sync --check    the same read, plus four failures: the store is unreachable, a declared model
                      is ABSENT, a model's content is EMPTY, or a model has not refreshed inside
                      --max-age-hours (default 48). Exit 3 unreachable, 4 absent, 6 refused content.

    Why a scheduled run matters: nothing else pulls content to disk. `read` caches opportunistically,
    so before this existed the disk copy was whatever happened to have been read - measured
    2026-10-22, one file from three days earlier - and nothing noticed a model that had stopped
    regenerating.
    """
    roles = discover_roles()
    declared = [(role, name) for role, name, _m, _p in roles]
    if args.model:
        wanted = set(args.model)
        declared = [(r, n) for r, n in declared if n in wanted or r in wanted]
        if not declared:
            warn(f"none of {sorted(wanted)} is a declared role or model; nothing to sync")
            return EXIT_USAGE

    try:
        items = list_models(cfg, DETAIL_CONTENT)
    except Unreachable:
        # The store could not be asked. Reported as an outage, and as nothing else: no claim is made
        # about any model.
        warn(f"the store did not answer at {cfg.url} — nothing is claimed about any model; this is "
             f"UNREACHABLE, not an empty bank")
        return EXIT_UNREACHABLE

    failures: list[str] = []
    rows: list[tuple[str, str, int, str, str, str]] = []
    for role, name in declared:
        found = find_model(items, name)
        if found is None:
            failures.append(f"{name}: ABSENT (declared by _aisdlc/{role}/MODEL.md, no model by that name)")
            rows.append((role, name, 0, "-", "-", "absent"))
            continue
        # split_content's first element is the EMPTY flag, not an "ok" flag: `(True, "")` means empty.
        empty, content = split_content(found)
        live = refreshed_at(found)
        if empty or not content.strip():
            failures.append(f"{name}: EMPTY (present, no content — still generating, or generation failed)")
            rows.append((role, name, 0, "-", live, "empty"))
            continue
        write_cache(name, live, content)
        digest = hashlib.sha256(content.encode("utf-8")).hexdigest()
        age_h = _age_hours(live)
        stale = age_h is not None and args.max_age_hours is not None and age_h > args.max_age_hours
        if stale:
            failures.append(
                f"{name}: STALE (last refreshed {live}, {age_h:.1f} h ago, limit {args.max_age_hours} h)"
            )
        rows.append((role, name, len(content.encode("utf-8")), digest, live, "stale" if stale else "ok"))

    width = max((len(n) for _r, n, *_ in rows), default=10)
    print(f"sync  bank={cfg.bank}  store={cfg.url}  cache={CACHE_DIR}")
    for role, name, size, digest, live, state in rows:
        print(f"  {state:6} {name:{width}}  {size:>7} B  {digest[:12]:12}  refreshed {live}  role {role}")
    if failures:
        print("")
        for f in failures:
            print(f"  FAIL  {f}")
    if args.check and failures:
        # 6 is "the store answered and refused or served nothing usable"; absent/unreachable keep their own codes.
        code = EXIT_UNREACHABLE if any("ABSENT" in f for f in failures) else EXIT_STORE_ERROR
        return code
    print("")
    print(f"  {len(rows)} model(s) synced to {CACHE_DIR}; {len(failures)} problem(s)")
    return EXIT_OK if not failures else EXIT_OK if not args.check else EXIT_STORE_ERROR


def _age_hours(stamp: str | None) -> float | None:
    """Hours since an ISO-8601 stamp, or None when it cannot be read."""
    if not stamp:
        return None
    try:
        then = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
    except ValueError:
        return None
    if then.tzinfo is None:
        then = then.replace(tzinfo=timezone.utc)
    return (datetime.now(timezone.utc) - then).total_seconds() / 3600.0


def build_parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    add_globals(common)
    for a in common._actions:
        if a.dest != "help":
            a.default = argparse.SUPPRESS

    p = argparse.ArgumentParser(
        prog="aisdlc",
        description="Read an agent-SDLC role's operating protocol, recall the record, edit the "
                    "prompt a protocol regenerates from. Plain HTTP, no dependencies.",
        epilog="Exit codes: 0 ok · 2 bad usage · 3 store unreachable · 4 model absent · 5 refused "
               "(needs --force) · 6 the store answered with an error.",
    )
    add_globals(p)
    sub = p.add_subparsers(dest="cmd", metavar="<command>")

    s = sub.add_parser("list", parents=[common],
                       help="what is in the bank (names, ids, refresh times, content state)")
    s.set_defaults(func=cmd_list)

    s = sub.add_parser("check", parents=[common],
                       help="the preflight: the configured bank exists, every declared model exists, "
                            "and each carries content (exit 6 when the bank does not)")
    s.set_defaults(func=cmd_check)

    s = sub.add_parser("read", parents=[common],
                       help="print a model's generated CONTENT (its operating protocol)")
    s.add_argument("-m", "--model", required=True, help="the model's name, e.g. developer_protocol")
    s.add_argument("-o", "--out", metavar="FILE", help="write the content to FILE instead of stdout")
    s.add_argument("--no-cache", action="store_true",
                   help="bypass the local cache entirely — neither read from it nor write to it")
    s.set_defaults(func=cmd_read)

    s = sub.add_parser("recall", parents=[common],
                       help="what the organisation already learned about a decision (precedent)")
    s.add_argument("-q", "--query", required=True, help="the decision at hand, in plain words")
    s.add_argument("--budget", metavar="LEVEL", help=f"retrieval effort: {' | '.join(BUDGETS)}"
                                                     f" (default mid)")
    s.add_argument("--types", metavar="LIST", help="comma-separated fact types to scope the recall "
                                                   f"to: {', '.join(FACT_TYPES)} (default all)")
    s.add_argument("--limit", type=int, default=DEFAULT_RECALL_LIMIT, metavar="N",
                   help=f"print at most N results (default {DEFAULT_RECALL_LIMIT}; 0 = all)")
    s.add_argument("--max-tokens", type=int, default=DEFAULT_RECALL_MAX_TOKENS, metavar="N",
                   help=f"the recall's own token budget (default {DEFAULT_RECALL_MAX_TOKENS})")
    s.add_argument("-o", "--out", metavar="FILE", help="write the results to FILE instead of stdout")
    s.set_defaults(func=cmd_recall)

    s = sub.add_parser("set-prompt", parents=[common],
                       help="replace a model's prompt (source_query) — the only thing it writes")
    s.add_argument("-m", "--model", required=True, help="the model's name")
    s.add_argument("-f", "--file", required=True, metavar="FILE",
                   help="the prompt file, e.g. _aisdlc/developer/PROMPT.md")
    s.add_argument("--force", action="store_true",
                   help="create the model if it does not exist (without this, an absent model is "
                        "REFUSED rather than created)")
    s.set_defaults(func=cmd_set_prompt)

    s = sub.add_parser("refresh", parents=[common],
                       help="ask the store to regenerate a model's content now")
    s.add_argument("-m", "--model", required=True, help="the model's name")
    s.set_defaults(func=cmd_refresh)

    s = sub.add_parser("seed", parents=[common],
                       help="create-or-update this project's role models from the tracked "
                            "PROMPT.md files (idempotent)")
    s.set_defaults(func=cmd_seed)

    s = sub.add_parser("sync", parents=[common],
                       help="pull every declared model to disk; --check fails when the loop is not alive")
    s.add_argument("-m", "--model", action="append", default=None,
                   help="one or more model names (or role directory names); default is every declared role")
    s.add_argument("--check", action="store_true",
                   help="exit non-zero on an unreachable store, an absent or empty model, or a stale one")
    s.add_argument("--max-age-hours", type=float, default=48.0,
                   help="how old a model's content may be before --check calls it STALE (default 48)")
    s.set_defaults(func=cmd_sync)

    s = sub.add_parser("cache", parents=[common], help="what is in the local content cache")
    s.add_argument("--clear", action="store_true", help="empty it")
    s.set_defaults(func=cmd_cache)

    return p


def main(argv: list[str]) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "cmd", None):
        parser.print_help(sys.stderr)
        return EXIT_USAGE
    try:
        cfg = resolve_config(args)
        if not getattr(args, "timeout", None):
            args.timeout = TIMEOUT_RECALL if args.cmd == "recall" else TIMEOUT_META
        return args.func(cfg, args)
    except UsageError as exc:
        print(f"aisdlc: {exc}", file=sys.stderr)
        return EXIT_USAGE
    except BankMissing as exc:
        # ONE LINE, naming the bank. Not the "Exit 6: …" paragraph below: this is a configuration fact
        # with a one-command fix, and a role has to be able to read it and act on it.
        print(f"aisdlc: {exc}", file=sys.stderr)
        return EXIT_STORE_ERROR
    except Unreachable as exc:
        print(f"aisdlc: STORE UNREACHABLE — {exc}.\n"
              f"        This is an outage, not a missing model (that would be exit 4). Say the "
              f"store could not be reached; do not report a protocol as absent.",
              file=sys.stderr)
        return EXIT_UNREACHABLE
    except StoreError as exc:
        print(f"aisdlc: the store was reached and refused to answer usefully — {exc}.\n"
              f"        Exit 6: the store ANSWERED, so this is not 'unreachable' (3) and not "
              f"'absent' (4).", file=sys.stderr)
        return EXIT_STORE_ERROR
    except KeyboardInterrupt:
        print("aisdlc: interrupted", file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
