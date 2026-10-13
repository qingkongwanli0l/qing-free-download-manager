import json
import os
import urllib.parse
import urllib.request

from yt_dlp.networking._urllib import UrllibRH as _UrllibRH
from yt_dlp.postprocessor.common import PostProcessor


def origin(url):
    value = urllib.parse.urlsplit(url)
    return value.scheme.lower(), value.hostname.lower(), value.port or (443 if value.scheme.lower() == 'https' else 80)


class QingOriginScopePP(PostProcessor):
    def __init__(self, downloader=None):
        super().__init__(downloader)
        config = json.loads(os.environ.pop('QDM_REQUEST_SCOPE'))
        source = origin(config['url'])
        headers = config['headers']

        class Scope(urllib.request.BaseHandler):
            handler_order = 1000

            def http_request(self, request):
                if origin(request.full_url) == source:
                    for name, value in headers.items():
                        request.remove_header(name.capitalize())
                        request.add_unredirected_header(name, value)
                return request

            https_request = http_request

        class UrllibRH(_UrllibRH):
            def _create_instance(self, *args, **kwargs):
                opener = super()._create_instance(*args, **kwargs)
                opener.add_handler(Scope())
                return opener

        previous = downloader.__dict__.pop('_request_director', None)
        if previous:
            previous.close()
        downloader._request_director = downloader.build_request_director([UrllibRH])
