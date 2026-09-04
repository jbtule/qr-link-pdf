# Environment for ./ci-local.sh - matches what the GitHub Actions runner
# provides, so the workflow's own steps can be replayed locally on Linux.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# node runs the wasm smoke test; python is required by Emscripten. The GitHub
# runner image has both already, a bare SDK image has neither.
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs python3 \
 && rm -rf /var/lib/apt/lists/* \
 && ln -sf /usr/bin/python3 /usr/bin/python

# ~1 GB, and the slow part of a cold run - baked into the image so repeat
# runs skip it.
RUN dotnet workload install wasm-tools

WORKDIR /src
