// The weather for the weather widget: Open-Meteo's forecast (no account), where the computer is:
// Windows' location service when it lets desktop apps use it (the place named by BigDataCloud's
// free reverse geocoding), else where the network connection is (ip-api.com, asked directly:
// through a proxy it would answer for the proxy's place). Fetched on a worker thread every half hour while a
// weather widget is on the desktop, and kept in %LOCALAPPDATA%\Atrium\weather.json, which the next
// start shows at once and the settings app's previews read.
#pragma once

#include <windows.h>
#include <winrt/Windows.Foundation.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace atrium {

struct WeatherHour {
    std::wstring label; // "现在", "14时", or the time of a sunrise / sunset
    int code = 0;       // WMO weather code
    bool day = true;
    float temp = 0;     // °C
    int event = 0;      // 1 sunrise, 2 sunset (code and temp unused)
};

struct WeatherDay {
    std::wstring label; // "今天", "周二"
    int code = 0;
    float lo = 0, hi = 0;
};

struct Weather {
    bool ok = false;
    std::wstring city;
    float temp = 0, hi = 0, lo = 0;
    int code = 0;
    bool day = true;
    std::vector<WeatherHour> hours; // the next six (sunrise and sunset among them)
    std::vector<WeatherDay> days;   // today and the next days
    std::wstring error;             // why there is none (shown while !ok)
    double lat = 0, lon = 0;        // where it is for (kept: the place again when no service can say)
    bool placed = false;
};

// "晴", "多云", "小雨"…
std::wstring WeatherCondition(int code);
std::wstring WeatherCachePath();

// Pure parsing helpers exposed for unit tests.
bool IsValidWeatherCoords(double lat, double lon);
bool ParseWeatherLocationJson(const std::string& body, int serviceIndex, double& lat, double& lon, std::wstring& city);
bool ParseWeatherForecastJson(const std::string& body, double lat, double lon, const std::wstring& city, bool english, Weather& out);
std::wstring WeatherToJsonString(const Weather& w);
bool WeatherFromJsonString(const std::wstring& text, Weather& out);

class WeatherSource {
public:
    using Fetcher = std::function<bool(const std::atomic_bool& quit, Weather& out)>;

    // `message` is posted to `notify` with a new Latest().
    WeatherSource(HWND notify, UINT message);
    WeatherSource(HWND notify, UINT message, Fetcher fetcher);
    ~WeatherSource();
    WeatherSource(const WeatherSource&) = delete;
    WeatherSource& operator=(const WeatherSource&) = delete;

    void SetActive(bool on); // fetch only while a weather widget shows
    void Refresh();          // the labels are language-specific: fetch again after a language change
    Weather Latest();

private:
    struct State;

    std::shared_ptr<State> state_;
    std::thread thread_;
};

// Waits on a WinRT async operation without holding `lock` across Status()/Cancel().
template<typename Async, typename KeepAlive> bool WaitWeatherAsync(const Async& op, const std::shared_ptr<KeepAlive>& keepAlive, std::mutex& lock,
                                                                   std::condition_variable& wake, const std::atomic_bool& quit,
                                                                   std::chrono::milliseconds timeout, bool* timedOut = nullptr)
{
    auto completed = std::make_shared<std::atomic_bool>(false);
    op.Completed([keepAlive, completed, &lock, &wake](const Async&, winrt::Windows::Foundation::AsyncStatus) {
        completed->store(true);
        std::lock_guard g(lock);
        wake.notify_all();
    });
    if (!completed->load() && op.Status() != winrt::Windows::Foundation::AsyncStatus::Started)
        return !quit.load();
    bool quitting = false;
    bool ok = false;
    {
        std::unique_lock g(lock);
        ok = wake.wait_for(g, timeout, [&] { return quit.load() || completed->load(); });
        quitting = quit.load();
    }
    if (quitting) {
        op.Cancel();
        return false;
    }
    if (!ok) {
        op.Cancel();
        if (timedOut) {
            *timedOut = true;
            return false;
        }
        throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
    }
    return true;
}

} // namespace atrium
