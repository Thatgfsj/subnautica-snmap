/* SNMapBoot.dll v3 - injected into Subnautica (Unity Mono, x64)
 * Loads the newest SNMapManaged*.dll through the game's own mono runtime and
 * calls SNMap.Boot.InstallMain() ON the game main thread (Unity API requirement).
 * Strategy: hook WH_GETMESSAGE on every thread of this process; only the thread
 * owning the game's main window performs the install. Diagnostics -> SNMapBoot.log.
 * Exported SNMapTrigger() lets the injector re-run the worker at any time
 * (hot update: drop a newer SNMapManaged*.dll in, trigger, done - no game restart).
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdarg.h>

typedef void  *(*mono_domain_assembly_open_t)(void *, const char *);
typedef void  *(*mono_assembly_get_image_t)(void *);
typedef void  *(*mono_class_from_name_t)(void *, const char *, const char *);
typedef void  *(*mono_class_get_method_from_name_t)(void *, const char *, int);
typedef void  *(*mono_runtime_invoke_t)(void *, void *, void **, void **);
typedef void  *(*mono_get_root_domain_t)(void);
typedef void  *(*mono_thread_attach_t)(void *);

static HMODULE g_self = NULL;
static HMODULE g_mono = NULL;
static HHOOK g_hooks[64];
static int g_hookCount = 0;
static volatile LONG g_state = 0;        /* 0=idle 1=armed 2=done 3=stopped */
static volatile LONG g_anyThread = 0;
static volatile LONG g_workerRunning = 0;
static DWORD g_mainTid = 0;

static void LaunchMapWindow(void);

static void dlog(const char *fmt, ...)
{
    wchar_t wpath[MAX_PATH];
    char path[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(g_self, wpath, MAX_PATH);
    if (!n || n >= MAX_PATH) return;
    while (n > 0 && wpath[n - 1] != L'\\') n--;
    if (n == 0) return;
    lstrcpyW(wpath + n, L"SNMapBoot.log");
    WideCharToMultiByte(CP_UTF8, 0, wpath, -1, path, (int)sizeof(path), NULL, NULL);
    FILE *f = fopen(path, "ab");
    if (!f) return;
    fprintf(f, "[%08u] ", (unsigned)GetTickCount());
    va_list ap;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fputc('\n', f);
    fclose(f);
}

static FARPROC mono_proc(const char *name)
{
    static const char *cands[] = {
        "mono-2.0-bdwgc.dll", "mono.dll", "mono-2.0.dll", "mono-1.0_vc.dll", NULL
    };
    int i;
    if (!g_mono) {
        for (i = 0; cands[i]; i++) {
            HMODULE m = GetModuleHandleA(cands[i]);
            if (m) { g_mono = m; break; }
        }
    }
    return g_mono ? GetProcAddress(g_mono, name) : NULL;
}

/* pick the newest SNMapManaged*.dll next to this dll */
static int resolve_managed_path(wchar_t *out, DWORD outChars)
{
    wchar_t dir[MAX_PATH];
    wchar_t pattern[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (!n || n >= MAX_PATH) return 0;
    while (n > 0 && dir[n - 1] != L'\\') n--;
    if (n == 0) return 0;
    dir[n] = 0;
    lstrcpyW(pattern, dir);
    lstrcpyW(pattern + n, L"SNMapManaged*.dll");
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return 0;
    FILETIME bestFt = fd.ftLastWriteTime;
    lstrcpyW(out, dir);
    lstrcpyW(out + n, fd.cFileName);
    while (FindNextFileW(h, &fd)) {
        if (CompareFileTime(&fd.ftLastWriteTime, &bestFt) > 0) {
            bestFt = fd.ftLastWriteTime;
            lstrcpyW(out, dir);
            lstrcpyW(out + n, fd.cFileName);
        }
    }
    FindClose(h);
    return 1;
}

static void do_install(void)
{
    mono_get_root_domain_t get_root;
    mono_thread_attach_t attach;
    mono_domain_assembly_open_t asm_open;
    mono_assembly_get_image_t get_image;
    mono_class_from_name_t cls_from_name;
    mono_class_get_method_from_name_t method_from_name;
    mono_runtime_invoke_t invoke;
    void *domain, *assembly, *image, *cls, *method;
    wchar_t wpath[MAX_PATH];
    char path[MAX_PATH * 2];

    get_root = (mono_get_root_domain_t)mono_proc("mono_get_root_domain");
    attach = (mono_thread_attach_t)mono_proc("mono_thread_attach");
    asm_open = (mono_domain_assembly_open_t)mono_proc("mono_domain_assembly_open");
    get_image = (mono_assembly_get_image_t)mono_proc("mono_assembly_get_image");
    cls_from_name = (mono_class_from_name_t)mono_proc("mono_class_from_name");
    method_from_name = (mono_class_get_method_from_name_t)mono_proc("mono_class_get_method_from_name");
    invoke = (mono_runtime_invoke_t)mono_proc("mono_runtime_invoke");
    if (!get_root || !attach || !asm_open || !get_image ||
        !cls_from_name || !method_from_name || !invoke) {
        dlog("install: missing mono export");
        return;
    }

    if (!resolve_managed_path(wpath, MAX_PATH)) {
        dlog("install: no SNMapManaged*.dll found");
        return;
    }
    WideCharToMultiByte(CP_UTF8, 0, wpath, -1, path, (int)sizeof(path), NULL, NULL);

    dlog("install: tid=%lu path=%s", (unsigned long)GetCurrentThreadId(), path);
    domain = get_root();
    dlog("install: domain=%p", domain);
    if (!domain) return;
    attach(domain);
    assembly = asm_open(domain, path);
    dlog("install: assembly=%p", assembly);
    if (!assembly) return;
    image = get_image(assembly);
    dlog("install: image=%p", image);
    if (!image) return;
    cls = cls_from_name(image, "SNMap", "Boot");
    dlog("install: class=%p", cls);
    if (!cls) return;
    method = method_from_name(cls, "InstallMain", 0);
    dlog("install: method=%p", method);
    if (!method) return;
    invoke(method, NULL, NULL, NULL);
    dlog("install: invoke returned");
    LaunchMapWindow();
}

/* 若地图窗口未运行则启动它(与本 dll 同目录的 SNMapWindow.exe) */
static void LaunchMapWindow(void)
{
    wchar_t dir[MAX_PATH];
    wchar_t exe[MAX_PATH];
    wchar_t cmd[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (!n || n >= MAX_PATH) return;
    while (n > 0 && dir[n - 1] != L'\\') n--;
    if (n == 0) return;
    dir[n] = 0;
    lstrcpyW(exe, dir);
    lstrcpyW(exe + n, L"SNMapWindow.exe");
    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES) return;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W pe;
        pe.dwSize = sizeof(pe);
        if (Process32FirstW(snap, &pe)) {
            do {
                if (lstrcmpiW(pe.szExeFile, L"SNMapWindow.exe") == 0) {
                    CloseHandle(snap);
                    dlog("map window already running");
                    return;
                }
            } while (Process32NextW(snap, &pe));
        }
        CloseHandle(snap);
    }

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
/* 若地图窗口未运行则启动它(加 --auto: 注入后待命隐藏, F9 才显示) */
static void LaunchMapWindow(void)
{
    wchar_t dir[MAX_PATH];
    wchar_t exe[MAX_PATH];
    wchar_t cmd[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (!n || n >= MAX_PATH) return;
    while (n > 0 && dir[n - 1] != L'\\') n--;
    if (n == 0) return;
    dir[n] = 0;
    lstrcpyW(exe, dir);
    lstrcpyW(exe + n, L"SNMapWindow.exe");
    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES) return;

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W pe;
        pe.dwSize = sizeof(pe);
        if (Process32FirstW(snap, &pe)) {
            do {
                if (lstrcmpiW(pe.szExeFile, L"SNMapWindow.exe") == 0) {
                    CloseHandle(snap);
                    dlog("map window already running");
                    return;
                }
            } while (Process32NextW(snap, &pe));
        }
        CloseHandle(snap);
    }

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    lstrcpyW(cmd, exe);
    lstrcpyW(cmd + lstrlenW(cmd), L" --auto");
    if (CreateProcessW(exe, cmd, NULL, NULL, FALSE, 0, NULL, dir, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        dlog("map window launched");
    } else {
        dlog("map window launch failed (GetLastError=%lu)", (unsigned long)GetLastError());
    }
}

static LRESULT CALLBACK hook_proc(int code, WPARAM wp, LPARAM lp)
{
    if (code >= 0 && g_state == 1) {
        DWORD tid = GetCurrentThreadId();
        if ((g_mainTid == 0 || g_anyThread || tid == g_mainTid) &&
            InterlockedCompareExchange(&g_state, 2, 1) == 1) {
            dlog("hook: firing on tid=%lu (main=%lu)", (unsigned long)tid, (unsigned long)g_mainTid);
            do_install();
        }
    }
    return CallNextHookEx(NULL, code, wp, lp);
}

struct find_ctx { DWORD pid; DWORD tid; LONG area; };

static BOOL CALLBACK enum_proc(HWND hwnd, LPARAM lp)
{
    struct find_ctx *ctx = (struct find_ctx *)lp;
    DWORD pid = 0, tid;
    RECT r;
    LONG area;
    if (!IsWindowVisible(hwnd)) return TRUE;
    tid = GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid || !tid) return TRUE;
    area = 0;
    if (GetWindowRect(hwnd, &r)) area = (LONG)(r.right - r.left) * (LONG)(r.bottom - r.top);
    if (area >= ctx->area) { ctx->area = area; ctx->tid = tid; }
    return TRUE;
}

static int enum_self_threads(DWORD *out, int max)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    DWORD pid = GetCurrentProcessId();
    int count = 0;
    THREADENTRY32 te;
    if (snap == INVALID_HANDLE_VALUE) return 0;
    te.dwSize = sizeof(te);
    if (Thread32First(snap, &te)) {
        do {
            if (te.th32OwnerProcessID == pid && count < max)
                out[count++] = te.th32ThreadID;
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
    return count;
}

static DWORD WINAPI worker(LPVOID arg)
{
    DWORD tids[128];
    int n, i, tries;

    (void)arg;
    InterlockedExchange(&g_state, 0);
    InterlockedExchange(&g_anyThread, 0);
    dlog("worker start");
    for (tries = 0; tries < 600; tries++) {
        if (mono_proc("mono_get_root_domain")) break;
        Sleep(200);
    }
    dlog("mono=%s", g_mono ? "found" : "MISSING");
    if (!g_mono) { InterlockedExchange(&g_workerRunning, 0); return 1; }

    {
        struct find_ctx ctx;
        ctx.pid = GetCurrentProcessId();
        ctx.tid = 0;
        ctx.area = -1;
        EnumWindows(enum_proc, (LPARAM)&ctx);
        g_mainTid = ctx.tid;
    }
    dlog("main tid=%lu", (unsigned long)g_mainTid);

    n = enum_self_threads(tids, 128);
    g_hookCount = 0;
    dlog("threads=%d", n);
    for (i = 0; i < n && g_hookCount < 64; i++) {
        HHOOK h = SetWindowsHookExW(WH_GETMESSAGE, hook_proc, g_self, tids[i]);
        if (h) g_hooks[g_hookCount++] = h;
    }
    dlog("hooks set=%d", g_hookCount);
    if (g_hookCount == 0) { InterlockedExchange(&g_workerRunning, 0); return 2; }

    InterlockedExchange(&g_state, 1);
    for (i = 0; i < 600 && g_state != 2; i++) Sleep(100);
    if (g_state != 2) {
        dlog("main-thread hook idle after 60s, allowing any thread");
        InterlockedExchange(&g_anyThread, 1);
        for (i = 0; i < 300 && g_state != 2; i++) Sleep(100);
    }
    InterlockedExchange(&g_state, 3); /* stop firing */
    for (i = 0; i < g_hookCount; i++) UnhookWindowsHookEx(g_hooks[i]);
    g_hookCount = 0;
    dlog("worker done (installed=%d)", g_state == 2);
    InterlockedExchange(&g_workerRunning, 0);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = hinst;
        DisableThreadLibraryCalls(hinst);
        if (InterlockedCompareExchange(&g_workerRunning, 1, 0) == 0)
            CreateThread(NULL, 0, worker, NULL, 0, NULL);
    }
    return TRUE;
}

/* exported: re-run the worker (injector calls this for hot updates) */
__declspec(dllexport) void __stdcall SNMapTrigger(void)
{
    if (InterlockedCompareExchange(&g_workerRunning, 1, 0) != 0) return;
    CreateThread(NULL, 0, worker, NULL, 0, NULL);
}
