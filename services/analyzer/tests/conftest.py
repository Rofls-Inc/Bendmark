from pathlib import Path


def pytest_configure(config):
    # Тестам сервера нужен код из proto; генерируем, если его ещё нет
    if not (Path(__file__).resolve().parents[1] / "generated" / "bendmark" / "v1" / "analyzer_pb2.py").exists():
        import gen_proto

        gen_proto.generate()
