import pathlib
file = r"F:\Coding Projects\RemotePlay\WebAssets\app.js"
lines = pathlib.Path(file).read_text(encoding="utf-8").splitlines()
for i,l in enumerate(lines):
    if "header row: favicon" in l or "sc-icon" in l:
        print(i+1, repr(l[:80]))
