"""Checks a Wavee pull request against the house flow before it is pushed or reviewed.

    python -I tools/pr-check.py                    the current branch against the open release branch
    python -I tools/pr-check.py --pr 238           a pull request by number (fetched; reads its base with gh)
    python -I tools/pr-check.py --head pr/238 --base origin/release/0.3.12 [--json] [--no-fetch]

What it checks (the flow: .claude/skills/wavee-contributing, docs/plans/wavee/release-flow-design.md):
  base        the PR targets the open release branch (the newest release/*), never main
  changelog   CHANGELOG.md is untouched: user-facing changes add a change file in changes/unreleased/
  contract    ContractVersion.cs is untouched, and a change under Theming/Contracts or Theming/Roles has a change
              file with contract: additive|breaking
  changes     the change files are valid (Wavee.ReleaseTool changes check), and a change to app code has one (warn)
  issues      every "Fixes #n" in the commits is in a change file's issues:, and the other way round (warn)
  engine      a changed engine.lock names a commit on fluent-gpu main or a wavee-hotfix/* branch
  private     no private material and no test files (the patterns of .githooks/pre-commit)

Exit 0 when nothing is an error, 1 when something is, 2 on a usage or git problem.
"""
import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

CLOSES = re.compile(r'\b(?:fix(?:es|ed)?|close[sd]?|resolve[sd]?)\s*:?\s+#(\d+)', re.I)
CONTRACT_DIRS = ('src/apps/Wavee/Theming/Contracts/', 'src/apps/Wavee/Theming/Roles/')
CONTRACT_VERSION = 'src/apps/Wavee/Theming/Contracts/ContractVersion.cs'
REPO = 'christosk92/WaveeMusic'


def git(*args, cwd=None, check=True):
    r = subprocess.run(['git', *args], cwd=cwd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout


def git_bytes(*args, cwd=None):
    r = subprocess.run(['git', *args], cwd=cwd, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.decode('utf-8', 'replace').strip()}")
    return r.stdout


def hook_patterns(root):
    """The private-path and test-file patterns of .githooks/pre-commit, so the two checks never drift apart.
    Returns (private, tests, tests_ok) compiled regexes, or Nones when the hook cannot be read."""
    path = os.path.join(root, '.githooks', 'pre-commit')
    try:
        text = open(path, encoding='utf-8').read()
    except OSError:
        return None, None, None

    def grab(name, flags=0):
        m = re.search(rf"^{name}='([^']*)'", text, re.M)
        return re.compile(m.group(1), flags) if m else None

    return grab('path_re', re.I), grab('test_re'), grab('test_ok')


def semver_key(name):
    m = re.search(r'release/(\d+)\.(\d+)\.(\d+)$', name)
    return tuple(int(x) for x in m.groups()) if m else (-1, -1, -1)


def open_release_branch(root):
    refs = [r.strip() for r in git('branch', '-r', '--list', 'origin/release/*', cwd=root).splitlines() if r.strip()]
    refs = [r for r in refs if semver_key(r) != (-1, -1, -1)]
    return max(refs, key=semver_key) if refs else None


class Report:
    def __init__(self):
        self.items = []

    def add(self, level, check, message):
        self.items.append({'level': level, 'check': check, 'message': message})

    def error(self, check, message):
        self.add('error', check, message)

    def warn(self, check, message):
        self.add('warn', check, message)

    def note(self, check, message):
        self.add('note', check, message)

    @property
    def errors(self):
        return sum(1 for i in self.items if i['level'] == 'error')


def pr_base_with_gh(root, number=None, branch=None):
    """The PR's base branch from gh, or None when gh or the PR is not there."""
    if not shutil.which('gh'):
        return None
    target = str(number) if number else branch
    if not target:
        return None
    r = subprocess.run(['gh', 'pr', 'view', target, '-R', REPO, '--json', 'baseRefName,number'], cwd=root,
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    if r.returncode != 0:
        return None
    try:
        return json.loads(r.stdout)
    except json.JSONDecodeError:
        return None


def change_files_check(root, head, paths):
    """Runs Wavee.ReleaseTool changes check over the head's changes/unreleased. Returns (result or None, why)."""
    if not shutil.which('dotnet'):
        return None, 'dotnet is not on PATH: the change files were not validated'
    tmp = tempfile.mkdtemp(prefix='wavee-pr-check-')
    try:
        data = git_bytes('archive', '--format=tar', head, 'changes/unreleased', cwd=root)
        with tarfile.open(fileobj=io.BytesIO(data)) as tar:
            try:
                tar.extractall(tmp, filter='data')
            except TypeError:   # Python before 3.12 has no extraction filters
                tar.extractall(tmp)
        project = os.path.join(root, 'src', 'apps', 'Wavee.ReleaseTool')
        r = subprocess.run(['dotnet', 'run', '--project', project, '-c', 'Release', '--', 'changes', 'check',
                            '--root', tmp, '--json'], cwd=root, capture_output=True, text=True, encoding='utf-8',
                           errors='replace')
        out = r.stdout.strip()
        start = out.rfind('\n{')
        payload = out[start + 1:] if start >= 0 else out
        try:
            return json.loads(payload), ''
        except json.JSONDecodeError:
            return None, f'Wavee.ReleaseTool changes check gave no JSON (exit {r.returncode}): {r.stderr.strip()[:300]}'
    except RuntimeError:
        return {'ok': True, 'files': [], 'problems': []}, ''   # the head has no changes/unreleased folder
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv):
    ap = argparse.ArgumentParser(description='Check a Wavee pull request against the house flow.')
    ap.add_argument('--pr', type=int, help='a pull request number: fetch it and check it against its base')
    ap.add_argument('--head', default='HEAD', help='the commit or ref to check (default HEAD)')
    ap.add_argument('--base', help='the base to compare with (default: the newest origin/release/*)')
    ap.add_argument('--json', action='store_true', help='print the findings as JSON')
    ap.add_argument('--no-fetch', action='store_true', help='do not fetch origin first')
    a = ap.parse_args(argv)

    try:
        root = git('rev-parse', '--show-toplevel').strip()
    except RuntimeError as e:
        print(e, file=sys.stderr)
        return 2
    rep = Report()

    try:
        if not a.no_fetch:
            git('fetch', '-q', 'origin', cwd=root)
        head = a.head
        if a.pr:
            git('fetch', '-q', 'origin', f'+refs/pull/{a.pr}/head:refs/remotes/pr/{a.pr}', cwd=root)
            head = f'pr/{a.pr}'
        release = open_release_branch(root)
        base = a.base or release
        if not base:
            print('no origin/release/* branch found; pass --base', file=sys.stderr)
            return 2
        mb = git('merge-base', base, head, cwd=root).strip()
    except RuntimeError as e:
        print(e, file=sys.stderr)
        return 2

    # base: the PR on GitHub targets the open release branch
    branch = None
    if not a.pr and head == 'HEAD':
        branch = git('rev-parse', '--abbrev-ref', 'HEAD', cwd=root).strip()
    info = pr_base_with_gh(root, a.pr, branch if branch and branch != 'HEAD' else None)
    want = release.split('/', 1)[1] if release else None
    if info:
        if want and info['baseRefName'] != want:
            rep.error('base', f"PR #{info['number']} targets {info['baseRefName']}; target {want} (the open release "
                              f"branch; main is what shipped and takes no PRs)")
        else:
            rep.note('base', f"PR #{info['number']} targets {info['baseRefName']}")
    elif a.base and want and not a.base.endswith(want):
        rep.warn('base', f'compared with {a.base}, but the open release branch is {want}')
    else:
        rep.note('base', f'no PR found for this branch; open it with --base {want}')

    status = git('diff', '--name-status', '--no-renames', mb, head, cwd=root)
    changed = {}
    for line in status.splitlines():
        parts = line.split('\t')
        if len(parts) >= 2:
            changed[parts[-1]] = parts[0]
    files = sorted(changed)
    log = git('log', '--format=%H%x00%B%x1e', f'{mb}..{head}', cwd=root)
    commits = [c.strip('\n') for c in log.split('\x1e') if c.strip()]
    closes = set()
    for c in commits:
        _, _, body = c.partition('\x00')
        closes.update(int(n) for n in CLOSES.findall(body))

    if not files:
        rep.note('diff', f'nothing differs from {base}')

    # changelog and contract version: never edited by hand
    if 'CHANGELOG.md' in changed:
        rep.error('changelog', 'CHANGELOG.md is edited: add a change file in changes/unreleased/ instead '
                               '(changes/README.md); the release assembles the CHANGELOG')
    if CONTRACT_VERSION in changed:
        rep.error('contract', 'ContractVersion.cs is edited: leave the number alone and set contract: additive|breaking '
                              'in the change file; the release raises the version')

    # change files
    change_paths = [f for f in files if f.startswith('changes/unreleased/') and f.endswith('.md')
                    and changed[f] != 'D']
    result = None
    if change_paths:
        result, why = change_files_check(root, head, change_paths)
        if result is None:
            rep.warn('changes', why)
    mine = []
    if result:
        for f in result.get('files', []):
            if f.get('path') in change_paths:
                mine.append(f)
                for p in f.get('problems', []):
                    rep.error('changes', f"{f['path']}: {p}")
        for p in result.get('problems', []):
            rep.error('changes', p)
    app_code = [f for f in files if f.startswith('src/apps/') and not f.endswith('.md')]
    if app_code and not change_paths:
        rep.warn('changes', f'{len(app_code)} app file(s) changed and no change file: a change a listener or theme '
                            f'author notices needs one (changes/README.md); refactors and tooling need none')

    contract_files = [f for f in files if f.startswith(CONTRACT_DIRS) and f != CONTRACT_VERSION]
    if contract_files:
        declared = [f for f in mine if f.get('contract') in ('additive', 'breaking')]
        if not declared and (result or not change_paths):
            rep.error('contract', f'{len(contract_files)} theme contract file(s) changed ({contract_files[0]}...) and no '
                                  f'change file says contract: additive|breaking')
        for f in declared:
            if f.get('contract') == 'breaking':
                rep.warn('contract', f"{f['path']} declares a breaking contract change: it needs the owner's agreement")

    # issues
    listed = set()
    for f in mine:
        listed.update(int(n) for n in f.get('issues', []))
    for n in sorted(closes - listed):
        if result is not None or not change_paths:
            rep.error('issues', f'a commit says it closes #{n} but no change file lists issues: [{n}]')
    for n in sorted(listed - closes):
        rep.warn('issues', f'a change file lists #{n} but no commit body says "Fixes #{n}" (GitHub will not close it)')

    # engine.lock
    if 'engine.lock' in changed and changed['engine.lock'] != 'D':
        text = git('show', f'{head}:engine.lock', cwd=root)
        sha = next((ln.strip() for ln in text.splitlines() if ln.strip() and not ln.lstrip().startswith('#')), '')
        engine = os.path.normpath(os.path.join(root, '..', 'fluent-gpu'))
        if not re.fullmatch(r'[0-9a-f]{40}', sha):
            rep.error('engine', f'engine.lock does not hold a 40-character commit sha ({sha[:50]!r})')
        elif not os.path.isdir(engine):
            rep.note('engine', f'engine.lock moved to {sha[:9]}; no ../fluent-gpu checkout to check it in')
        else:
            try:
                if not a.no_fetch:
                    git('fetch', '-q', 'origin', cwd=engine)
                where = git('branch', '-r', '--contains', sha, cwd=engine, check=False)
                refs = [r.strip() for r in where.splitlines()]
                if not any(r == 'origin/main' or r.startswith('origin/wavee-hotfix/') for r in refs):
                    rep.error('engine', f'engine.lock names {sha[:9]}, which is not on fluent-gpu main or a '
                                        f'wavee-hotfix/* branch: merge the engine PR first')
                else:
                    rep.note('engine', f'engine.lock moved to {sha[:9]} (on fluent-gpu main)')
            except RuntimeError as e:
                rep.note('engine', f'could not check engine.lock: {e}')

    # private material and tests: the pre-commit hook's own patterns
    private, tests, tests_ok = hook_patterns(root)
    if private is None or tests is None:
        rep.note('private', '.githooks/pre-commit could not be read: private paths and test files were not checked')
    for f in files:
        if changed[f] == 'D':
            continue
        if private is not None and private.search(f):
            rep.error('private', f'{f} is private material (.githooks/pre-commit): it never enters this repo')
        if tests is not None and tests.search(f) and not (tests_ok is not None and tests_ok.search(f)):
            rep.error('private', f'{f} is a test file: tests are banned (verify with the headless inspector)')

    if a.json:
        print(json.dumps({'base': base, 'head': head, 'mergeBase': mb, 'files': len(files), 'commits': len(commits),
                          'errors': rep.errors, 'items': rep.items}, indent=1))
    else:
        print(f'pr-check: {head} against {base} ({len(files)} files, {len(commits)} commits)')
        for i in rep.items:
            print(f"  {i['level']:<5} {i['check']:<9} {i['message']}")
        print(f'  {rep.errors} error(s)' if rep.errors else '  no errors')
    return 1 if rep.errors else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
