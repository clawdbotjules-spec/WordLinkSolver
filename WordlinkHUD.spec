# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec: bundles the HUD, the OCR helper and the dictionary into
# a self-contained macOS app (dist/WordlinkHUD.app).

a = Analysis(
    ['hud.py'],
    pathex=[],
    binaries=[('fast_ocr', '.')],
    datas=[('game_words.txt', '.')],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='WordlinkHUD',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='WordlinkHUD',
)
app = BUNDLE(
    coll,
    name='WordlinkHUD.app',
    icon=None,
    bundle_identifier=None,
)
