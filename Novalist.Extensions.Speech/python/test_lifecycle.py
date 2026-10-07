"""Worker completion and offline readiness regressions without model downloads."""

import io
import json
from pathlib import Path
import socket
import tempfile
import unittest
from unittest.mock import MagicMock, patch

from test_sidecar import sidecar


class LifecycleTests(unittest.TestCase):
    def test_fatal_render_error_still_terminates_its_protocol_response(self):
        with tempfile.TemporaryDirectory() as root, \
                patch.object(sidecar.sys, "argv", ["sidecar", "--work", root]), \
                patch.object(sidecar.sys, "stdin", io.StringIO('{"op":"render","id":"9"}\n')), \
                patch.object(sidecar, "new_engine", return_value=sidecar.Engine("cpu", None)), \
                patch.object(sidecar, "do_render", side_effect=RuntimeError("model load failed")), \
                patch.object(sidecar, "note"), patch.object(sidecar, "emit") as emit:
            self.assertEqual(0, sidecar.main())
        self.assertEqual(["error", "done"], [call.kwargs["type"] for call in emit.call_args_list])

    def test_unprepared_engine_never_downloads_implicitly(self):
        with patch.object(sidecar, "download_checkpoint") as download:
            with self.assertRaisesRegex(RuntimeError, "models-not-prepared"):
                sidecar._load_checkpoint(sidecar.Engine("cpu", None), sidecar.CLONE_MODEL, "clone")
            download.assert_not_called()

    def test_complete_preparation_survives_restart_and_loads_offline(self):
        with tempfile.TemporaryDirectory() as root:
            work = str(Path(root, "work"))
            engine = sidecar.Engine("cpu", None)
            for index, model_id in enumerate((sidecar.DESIGN_MODEL, sidecar.CLONE_MODEL)):
                snapshot = Path(root, str(index))
                snapshot.mkdir()
                (snapshot / "weights.bin").write_bytes(b"weights")
                engine.snapshots[model_id] = str(snapshot)
            sidecar.save_models(engine, work)
            restarted = sidecar.Engine("cpu", None)
            sidecar.restore_models(restarted, work)
            self.assertEqual(engine.snapshots, restarted.snapshots)
            qwen = MagicMock()
            with patch.dict(sidecar.sys.modules, qwen_tts=qwen), \
                    patch.object(socket, "create_connection", side_effect=AssertionError("network is disabled")), \
                    patch.object(socket.socket, "connect", side_effect=AssertionError("network is disabled")), \
                    patch.object(sidecar, "download_checkpoint") as download, patch.object(sidecar, "emit"):
                sidecar._load_checkpoint(restarted, sidecar.CLONE_MODEL, "clone")
            download.assert_not_called()
            self.assertEqual(engine.snapshots[sidecar.CLONE_MODEL],
                             qwen.Qwen3TTSModel.from_pretrained.call_args.args[0])
            (Path(engine.snapshots[sidecar.CLONE_MODEL]) / "weights.bin").unlink()
            incomplete = sidecar.Engine("cpu", None)
            sidecar.restore_models(incomplete, work)
            self.assertEqual({}, incomplete.snapshots)

    def test_readiness_for_another_backend_is_not_reused(self):
        with tempfile.TemporaryDirectory() as root:
            Path(root, "models-ready.json").write_text(json.dumps({"backend": "mlx", "models": {}}))
            engine = sidecar.Engine("cpu", None)
            sidecar.restore_models(engine, str(Path(root, "work")))
            self.assertEqual({}, engine.snapshots)


if __name__ == "__main__":
    unittest.main()
