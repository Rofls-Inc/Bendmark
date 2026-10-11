"""Генерирует Python-код из proto/bendmark/v1/*.proto в services/analyzer/generated/.

Сгенерированное не коммитится (каталог в .gitignore). Запуск из services/analyzer:
    python gen_proto.py
Нужен grpcio-tools (есть в requirements-dev.txt).
"""

import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROTO_ROOT = ROOT.parents[1] / "proto"
GENERATED = ROOT / "generated"


def generate() -> Path:
    from grpc_tools import protoc

    sources = sorted(PROTO_ROOT.glob("bendmark/v1/*.proto"))
    if not sources:
        raise SystemExit(f"Не найдены .proto в {PROTO_ROOT / 'bendmark' / 'v1'}")

    shutil.rmtree(GENERATED, ignore_errors=True)
    GENERATED.mkdir(parents=True)
    include = Path(protoc.__file__).resolve().parent / "_proto"
    # Пути внутри -I для protoc всегда через "/", в том числе на Windows
    files = [source.relative_to(PROTO_ROOT).as_posix() for source in sources]
    code = protoc.main([
        "grpc_tools.protoc",
        f"-I{PROTO_ROOT}",
        f"-I{include}",
        f"--python_out={GENERATED}",
        f"--grpc_python_out={GENERATED}",
        *files,
    ])
    if code != 0:
        raise SystemExit(f"protoc завершился с кодом {code}")
    return GENERATED


if __name__ == "__main__":
    print(f"Код из proto записан в {generate()}")
    sys.exit(0)
