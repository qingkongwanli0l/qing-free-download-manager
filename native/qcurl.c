/* Copyright (c) 2026 Qing Free Download Manager contributors. MIT. */
#define QCURL_BUILD
#include "qcurl.h"
#include <stdlib.h>
#include <curl/curl.h>

typedef char qcurl_off_t_is_64[(sizeof(curl_off_t) == sizeof(int64_t)) ? 1 : -1];
typedef char qcurl_long_is_32[(sizeof(long) == sizeof(int32_t)) ? 1 : -1];

struct qcurl_request {
    CURL *easy;
    CURLM *multi;
    struct curl_slist *headers;
    int started;
    int done;
    CURLcode result;
    char error[CURL_ERROR_SIZE];
};

int32_t QCURL_CALL qcurl_global_init(void) { return curl_global_init(CURL_GLOBAL_DEFAULT); }
void QCURL_CALL qcurl_global_cleanup(void) { curl_global_cleanup(); }

qcurl_request *QCURL_CALL qcurl_create(void)
{
    qcurl_request *request = calloc(1, sizeof(*request));
    if(!request) return NULL;
    request->easy = curl_easy_init();
    request->multi = curl_multi_init();
    if(!request->easy || !request->multi) { qcurl_destroy(request); return NULL; }
    curl_easy_setopt(request->easy, CURLOPT_ERRORBUFFER, request->error);
    return request;
}

void QCURL_CALL qcurl_destroy(qcurl_request *request)
{
    if(!request) return;
    if(request->started) curl_multi_remove_handle(request->multi, request->easy);
    if(request->easy) curl_easy_cleanup(request->easy);
    if(request->multi) curl_multi_cleanup(request->multi);
    curl_slist_free_all(request->headers);
    free(request);
}

int32_t QCURL_CALL qcurl_set_long(qcurl_request *request, int32_t option, int32_t value)
{
    switch(option) {
    case CURLOPT_HTTPGET: case CURLOPT_POST: case CURLOPT_NOPROGRESS:
    case CURLOPT_FOLLOWLOCATION: case CURLOPT_CONNECTTIMEOUT_MS: case CURLOPT_SUPPRESS_CONNECT_HEADERS:
        return curl_easy_setopt(request->easy, (CURLoption)option, (long)value);
    default: return CURLE_UNKNOWN_OPTION;
    }
}

int32_t QCURL_CALL qcurl_set_off_t(qcurl_request *request, int32_t option, int64_t value)
{
    switch(option) {
    case CURLOPT_POSTFIELDSIZE_LARGE: case CURLOPT_MAX_RECV_SPEED_LARGE:
        return curl_easy_setopt(request->easy, (CURLoption)option, (curl_off_t)value);
    default: return CURLE_UNKNOWN_OPTION;
    }
}

int32_t QCURL_CALL qcurl_set_string(qcurl_request *request, int32_t option, const char *value)
{
    switch(option) {
    case CURLOPT_URL: case CURLOPT_PROXY: case CURLOPT_NOPROXY: case CURLOPT_USERPWD:
    case CURLOPT_REFERER: case CURLOPT_USERAGENT: case CURLOPT_CAINFO: case CURLOPT_CUSTOMREQUEST:
        return curl_easy_setopt(request->easy, (CURLoption)option, value);
    default: return CURLE_UNKNOWN_OPTION;
    }
}

int32_t QCURL_CALL qcurl_add_header(qcurl_request *request, const char *header)
{
    struct curl_slist *list = curl_slist_append(request->headers, header);
    if(!list) return CURLE_OUT_OF_MEMORY;
    request->headers = list;
    return curl_easy_setopt(request->easy, CURLOPT_HTTPHEADER, list);
}

int32_t QCURL_CALL qcurl_set_callbacks(qcurl_request *request, qcurl_io write, qcurl_io read, qcurl_io header, qcurl_progress progress, void *user)
{
    CURLcode result;
#define SET_CALLBACK(option, value) do { result = curl_easy_setopt(request->easy, option, value); if(result) return result; } while(0)
    SET_CALLBACK(CURLOPT_WRITEFUNCTION, write);
    SET_CALLBACK(CURLOPT_READFUNCTION, read);
    SET_CALLBACK(CURLOPT_HEADERFUNCTION, header);
    SET_CALLBACK(CURLOPT_XFERINFOFUNCTION, progress);
    SET_CALLBACK(CURLOPT_WRITEDATA, user);
    SET_CALLBACK(CURLOPT_READDATA, user);
    SET_CALLBACK(CURLOPT_HEADERDATA, user);
    SET_CALLBACK(CURLOPT_XFERINFODATA, user);
#undef SET_CALLBACK
    return CURLE_OK;
}

int32_t QCURL_CALL qcurl_start(qcurl_request *request)
{
    CURLMcode result = curl_multi_add_handle(request->multi, request->easy);
    if(result == CURLM_OK) request->started = 1;
    return result;
}

int32_t QCURL_CALL qcurl_step(qcurl_request *request, int32_t max_wait_ms, int32_t *running, int32_t *result)
{
    int active = 0, remaining, count;
    CURLMsg *message;
    CURLMcode code = curl_multi_perform(request->multi, &active);
    if(code != CURLM_OK) return code;
    while((message = curl_multi_info_read(request->multi, &remaining))) {
        if(message->msg == CURLMSG_DONE) { request->done = 1; request->result = message->data.result; }
    }
    *running = active;
    *result = request->done ? (int32_t)request->result : -1;
    if(active && max_wait_ms > 0) code = curl_multi_poll(request->multi, NULL, 0, max_wait_ms, &count);
    return code;
}

int32_t QCURL_CALL qcurl_pause(qcurl_request *request, int32_t mask)
{ return curl_easy_pause(request->easy, mask); }

const char *QCURL_CALL qcurl_error(qcurl_request *request) { return request->error; }
