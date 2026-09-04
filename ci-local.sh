#!/bin/sh
# Runs the GitHub Actions build job locally, on Linux, in a container.
#
# Rather than reimplementing the workflow, this extracts and replays that job's
# own `run:` steps, so the two cannot drift. Uncommitted changes are included,
# which is the point - it answers "would CI pass?" before pushing.
#
# Needs Apple's `container` (macOS 26+) or Docker. First run builds an image
# with the wasm-tools workload baked in and takes a few minutes; later runs
# reuse it.
set -eu

root=$(cd "$(dirname "$0")" && pwd)
workflow="$root/.github/workflows/deploy.yml"
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

# Pull the build job's shell steps straight out of the workflow.
python3 - "$workflow" > "$stage/.ci-steps.sh" <<'PY'
import sys, yaml
w = yaml.safe_load(open(sys.argv[1]))
print("set -eu")
for name, value in (w.get("env") or {}).items():
    print(f'export {name}="{value}"')
for step in w["jobs"]["build"]["steps"]:
    if "run" in step:
        print(f'\necho "::: {step.get("name", "run")}"')
        print(step["run"])
PY

# wasm-opt needs room during the Release Emscripten link; with the default
# container memory it is killed with SIGKILL partway through.
echo "==> replaying the build job on linux"
$runtime run --rm \
    --cpus 4 --memory 8g \
    --mount type=virtiofs,source="$stage",target=/src \
    --workdir /src \
    "$image" bash /src/.ci-steps.sh  # bash, matching the default shell for `run:` steps
