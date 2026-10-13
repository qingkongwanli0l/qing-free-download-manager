/* Copyright (c) 2026 Qing Free Download Manager contributors. MIT. */
#ifndef QCURL_H
#define QCURL_H
#include <stddef.h>
#include <stdint.h>
#ifdef QCURL_BUILD
#define QCURL_API __declspec(dllexport)
#else
#define QCURL_API __declspec(dllimport)
#endif
#define QCURL_CALL __cdecl

typedef struct qcurl_request qcurl_request;
typedef size_t (QCURL_CALL *qcurl_io)(char *, size_t, size_t, void *);
typedef int (QCURL_CALL *qcurl_progress)(void *, int64_t, int64_t, int64_t, int64_t);

QCURL_API int32_t QCURL_CALL qcurl_global_init(void);
QCURL_API void QCURL_CALL qcurl_global_cleanup(void);
QCURL_API qcurl_request *QCURL_CALL qcurl_create(void);
QCURL_API void QCURL_CALL qcurl_destroy(qcurl_request *request);
QCURL_API int32_t QCURL_CALL qcurl_set_long(qcurl_request *request, int32_t option, int32_t value);
QCURL_API int32_t QCURL_CALL qcurl_set_off_t(qcurl_request *request, int32_t option, int64_t value);
QCURL_API int32_t QCURL_CALL qcurl_set_string(qcurl_request *request, int32_t option, const char *value);
QCURL_API int32_t QCURL_CALL qcurl_add_header(qcurl_request *request, const char *header);
QCURL_API int32_t QCURL_CALL qcurl_set_callbacks(qcurl_request *request, qcurl_io write, qcurl_io read, qcurl_io header, qcurl_progress progress, void *user);
QCURL_API int32_t QCURL_CALL qcurl_start(qcurl_request *request);
QCURL_API int32_t QCURL_CALL qcurl_step(qcurl_request *request, int32_t max_wait_ms, int32_t *running, int32_t *result);
QCURL_API int32_t QCURL_CALL qcurl_pause(qcurl_request *request, int32_t mask);
QCURL_API const char *QCURL_CALL qcurl_error(qcurl_request *request);
#endif
