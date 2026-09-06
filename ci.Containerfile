# Environment for ./ci-local.sh - matches what the GitHub Actions runner
# provides, so the workflow's own steps can be replayed locally on Linux.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# node runs the wasm smoke test and fetches the OCR checkbox's tesseract-wasm
# assets (npm pack); python is required by Emscripten. The GitHub runner
# image has all of these already, a bare SDK image has none.
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs npm python3 \
 && rm -rf /var/lib/apt/lists/* \
 && ln -sf /usr/bin/python3 /usr/bin/python

# ~1 GB, and the slow part of a cold run - baked into the image so repeat
# runs skip it.
RUN dotnet workload install wasm-tools

WORKDIR /src
