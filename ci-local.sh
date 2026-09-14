#!/bin/sh
# Runs the GitHub Actions build/test/deploy-prep steps locally, on Linux, in
# a container - the same steps a push to main runs, across both workflow
# files (test.yml's own "build" job, then deploy.yml's "build" job, which
# `uses:` test.yml on GitHub but that layer doesn't apply here).
#
# Rather than reimplementing the workflows, this extracts and replays each
# job's own `run:` steps in order, so the two cannot drift. Uncommitted
# changes are included, which is the point - it answers "would CI pass?"
# before pushing.
#
# Needs Apple's `container` (macOS 26+) or Docker. First run builds an image
# with the wasm-tools workload baked in and takes a few minutes; later runs
# reuse it.
set -eu

root=$(cd "$(dirname "$0")" && pwd)
workflows="$root/.github/workflows/test.yml $root/.github/workflows/deploy.yml"
image=qr-link-pdf-ci

if command -v container > /dev/null 2>&1; then
    runtime=container
elif command -v docker > /dev/null 2>&1; then
    runtime=docker
else
    echo "need Apple's container CLI or docker" >&2
    exit 1
fi

if [ "$runtime" = container ] && ! container system status > /dev/null 2>&1; then
    echo "==> starting the container service"
    container system start
fi

echo "==> building $image (cached after the first run)"
$runtime build -t "$image" -f "$root/ci.Containerfile" "$root" > /dev/null

# Stage tracked *and* uncommitted files, minus anything gitignored, so the
# container sees the working tree rather than HEAD.
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
(cd "$root" && git ls-files -c -o --exclude-standard | tar -cf - -T -) | tar -xf - -C "$stage"

# Pull each workflow's build job's shell steps straight out of the files,
# in order (test.yml first, then deploy.yml - the same order GitHub runs
# them in via deploy.yml's `uses:`).
python3 - $workflows > "$stage/.ci-steps.sh" <<'PY'
import sys, yaml
print("set -eu")
for path in sys.argv[1:]:
    w = yaml.safe_load(open(path))
    for name, value in (w.get("env") or {}).items():
        print(f'export {name}="{value}"')
    for step in w["jobs"]["build"]["steps"]:
        if "run" in step:
            print(f'\necho "::: {step.get("name", "run")}"')
            print(step["run"])
PY

# wasm-opt needs room during the Release Emscripten link; with the default
# container memory it is killed with SIGKILL partway through.
echo "==> replaying the build/test jobs on linux"
$runtime run --rm \
    --cpus 4 --memory 8g \
    --mount type=virtiofs,source="$stage",target=/src \
    --workdir /src \
    "$image" bash /src/.ci-steps.sh  # bash, matching the default shell for `run:` steps
