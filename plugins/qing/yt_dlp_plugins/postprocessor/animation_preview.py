import base64
import os
import queue
import subprocess
import threading
from yt_dlp.downloader.fragment import FragmentFD
from yt_dlp.postprocessor.common import PostProcessor


class _Preview:
    def __init__(self, downloader):
        self.downloader = downloader
        self.pending = queue.Queue(2)
        self.stopped = threading.Event()
        self.retained = 0
        self.lock = threading.Lock()
        args = [os.environ['QDM_ANIMATION_FFMPEG'], '-hide_banner', '-loglevel', 'quiet', '-nostdin',
                '-protocol_whitelist', 'pipe', '-format_whitelist', 'mov,mpegts,matroska,webm',
                '-probesize', '32768', '-analyzeduration', '0', '-skip_frame', 'nokey', '-i', 'pipe:0',
                '-map', '0:v:0', '-an', '-sn', '-vf', 'scale=320:180:force_original_aspect_ratio=decrease',
                '-vsync', '0', '-flush_packets', '1', '-f', 'image2pipe', '-vcodec', 'bmp', 'pipe:1']
        self.process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        self.writer = threading.Thread(target=self.write, daemon=True)
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.writer.start()
        self.reader.start()

    def stop(self):
        self.stopped.set()
        try:
            self.process.kill()
        except OSError:
            pass

    def offer(self, data):
        if self.stopped.is_set():
            return
        with self.lock:
            if self.retained + len(data) > 4 * 1024 * 1024:
                self.stop()
                return
            self.retained += len(data)
        try:
            self.pending.put_nowait(data)
        except queue.Full:
            self.stop()

    def write(self):
        try:
            while not self.stopped.is_set():
                try:
                    data = self.pending.get(timeout=0.1)
                except queue.Empty:
                    continue
                if data is None:
                    break
                self.process.stdin.write(data)
                self.process.stdin.flush()
                size = len(data)
                data = None
                with self.lock:
                    self.retained -= size
        except (OSError, ValueError):
            self.stop()
        finally:
            try:
                self.process.stdin.close()
            except OSError:
                pass

    def exact(self, count):
        result = bytearray()
        while len(result) < count:
            block = self.process.stdout.read(count - len(result))
            if not block:
                return None
            result.extend(block)
        return result

    def read(self):
        try:
            while not self.stopped.is_set():
                header = self.exact(6)
                if header is None:
                    break
                size = int.from_bytes(header[2:6], 'little')
                if header[:2] != b'BM' or not 54 <= size <= 1000000:
                    break
                body = self.exact(size - 6)
                if body is None:
                    break
                self.downloader.to_stdout('QDM_ANIMATION:' + base64.b64encode(header + body).decode('ascii'))
        except (OSError, ValueError):
            pass
        finally:
            self.stop()
            self.process.stdout.close()

    def finish(self):
        if not self.stopped.is_set():
            try:
                self.pending.put_nowait(None)
            except queue.Full:
                self.stop()
        self.writer.join(0.5)
        self.reader.join(0.5)
        self.stop()
        self.writer.join()
        self.reader.join()
        self.process.wait()
        with self.pending.mutex:
            self.pending.queue.clear()


class QingAnimationPP(PostProcessor):
    def __init__(self, downloader=None):
        super().__init__(downloader)
        self.preview = None
        self.started = False
        start = FragmentFD._start_frag_download
        append = FragmentFD._append_fragment

        def start_fragment(fd, ctx, info):
            result = start(fd, ctx, info)
            if fd.ydl is downloader and not self.started and info.get('vcodec') != 'none' and not ctx.get('complete_frags_downloaded_bytes'):
                self.started = True
                ctx['_qing_animation'] = True
            return result

        def append_fragment(fd, ctx, data):
            result = append(fd, ctx, data)
            if ctx.get('_qing_animation'):
                try:
                    if self.preview is None:
                        self.preview = _Preview(downloader)
                    self.preview.offer(data)
                except Exception:
                    ctx['_qing_animation'] = False
                    if self.preview is not None:
                        self.preview.stop()
            return result

        FragmentFD._start_frag_download = start_fragment
        FragmentFD._append_fragment = append_fragment
        downloader.add_progress_hook(self.progress)

    def progress(self, status):
        if status.get('status') == 'finished' and self.preview is not None:
            try:
                self.preview.finish()
            except Exception:
                self.preview.stop()
