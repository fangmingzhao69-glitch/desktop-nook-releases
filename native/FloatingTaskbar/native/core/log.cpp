#include "log.h"
#include "zheye_host.h"

#include <windows.h>
#include <shlobj.h>

#include <cstdarg>
#include <cstdio>
#include <share.h>
#include <condition_variable>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace atrium {

namespace {

std::wstring LogPath()
{
    std::wstring dir = zheye::DataDirectory();
    CreateDirectoryW(dir.c_str(), nullptr);
    dir += L"\\logs";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\core.log";
}

// Lines are written by a thread of their own into a file kept open: opening
// and closing it for every line (a virus scan each time) took milliseconds,
// under a lock every thread shared - animations stalled on their own log
// lines. A file over 8 MB is moved to core.1.log at start and on overflow.
class Writer {
public:
    static Writer& Get()
    {
        static Writer w;
        return w;
    }
    void Add(std::wstring line)
    {
        constexpr size_t kMaxQueue = 4096;
        {
            std::lock_guard g(mutex_);
            if (queue_.size() >= kMaxQueue) {
                ++dropped_;
                return;
            }
            queue_.push_back(std::move(line));
        }
        cv_.notify_one();
    }

    Writer(const Writer&) = delete;
    Writer& operator=(const Writer&) = delete;

private:
    Writer() : path_(LogPath()), thread_([this] { Run(); }) {}
    ~Writer()
    {
        {
            std::lock_guard g(mutex_);
            stop_ = true;
        }
        cv_.notify_one();
        if (thread_.joinable())
            thread_.join();
    }
    void Run()
    {
        SetThreadDescription(GetCurrentThread(), L"Atrium log");
        constexpr unsigned long long kMaxBytes = 8ULL << 20;
        const std::wstring& path = path_;
        const std::wstring backup = path.substr(0, path.size() - 4) + L".1.log";
        unsigned long long written = 0;
        WIN32_FILE_ATTRIBUTE_DATA a{};
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &a)) {
            const unsigned long long size = (static_cast<unsigned long long>(a.nFileSizeHigh) << 32) | a.nFileSizeLow;
            if (size > kMaxBytes)
                MoveFileExW(path.c_str(), backup.c_str(), MOVEFILE_REPLACE_EXISTING);
            else
                written = size;
        }
        FILE* f = _wfsopen(path.c_str(), L"a, ccs=UTF-8", _SH_DENYNO); // others may read it meanwhile
        std::vector<std::wstring> lines;
        for (;;) {
            size_t dropped = 0;
            {
                std::unique_lock g(mutex_);
                cv_.wait(g, [&] { return stop_ || !queue_.empty() || dropped_ > 0; });
                lines.swap(queue_);
                dropped = dropped_;
                dropped_ = 0;
                if (lines.empty() && dropped == 0 && stop_)
                    break;
            }
            if (dropped > 0) {
                SYSTEMTIME t;
                GetLocalTime(&t);
                wchar_t summary[128];
                swprintf_s(summary, L"%04u-%02u-%02u %02u:%02u:%02u.%03u [%lu] log: dropped %zu lines (queue full)\n", t.wYear, t.wMonth, t.wDay, t.wHour,
                           t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentThreadId(), dropped);
                lines.emplace_back(summary);
            }
            if (f) {
                for (const std::wstring& l : lines) {
                    fputws(l.c_str(), f);
                    const int bytes = WideCharToMultiByte(CP_UTF8, 0, l.data(), int(l.size()), nullptr, 0, nullptr, nullptr);
                    if (bytes > 0)
                        written += unsigned(bytes);
                    if (written > kMaxBytes) {
                        fflush(f);
                        fclose(f);
                        MoveFileExW(path.c_str(), backup.c_str(), MOVEFILE_REPLACE_EXISTING);
                        f = _wfsopen(path.c_str(), L"a, ccs=UTF-8", _SH_DENYNO);
                        written = 0;
                        if (!f)
                            break;
                    }
                }
                if (f)
                    fflush(f);
            }
            lines.clear();
        }
        if (f)
            fclose(f);
    }

    std::mutex mutex_;
    std::condition_variable cv_;
    std::vector<std::wstring> queue_;
    size_t dropped_ = 0;
    bool stop_ = false;
    std::wstring path_;
    std::thread thread_;
};

} // namespace

void Log(const wchar_t* format, ...)
{
    wchar_t text[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(text, _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME t;
    GetLocalTime(&t);
    wchar_t line[1200];
    swprintf_s(line, L"%04u-%02u-%02u %02u:%02u:%02u.%03u [%lu] %s\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
               GetCurrentThreadId(), text);
    OutputDebugStringW(line);
    Writer::Get().Add(line);
}

} // namespace atrium
