import pathlib

sln_path = pathlib.Path('MusicBeeChromecast.sln')
if not sln_path.exists():
    raise SystemExit('MusicBeeChromecast.sln not found')

t = sln_path.read_text(encoding='utf-8', errors='ignore')
old = 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CSharpDll", "CSharpDll.csproj", "{F5D46BA1-6F21-40EF-9695-46105CCACD08}"'
new = 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MusicBeeChromecast", "MusicBeeChromecast.csproj", "{F5D46BA1-6F21-40EF-9695-46105CCACD08}"'

t2 = t.replace(old, new)
if t2 == t:
    raise SystemExit('No changes made (pattern not found)')

sln_path.write_text(t2, encoding='utf-8')
print('Updated solution project entry')
