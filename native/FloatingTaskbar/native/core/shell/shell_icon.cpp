// Shell item and executable icon loading.

#include "shell/shell_icon.h"

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <winrt/base.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace atrium {

namespace {

// 32bpp top-down BGRA copy of `bmp`, as the driver stores it (alpha untouched).
bool ReadBits(HBITMAP bmp, int& w, int& h, std::vector<uint8_t>& out)
{
    BITMAP bm{};
    if (!GetObjectW(bmp, sizeof(bm), &bm) || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        return false;
    w = bm.bmWidth;
    h = bm.bmHeight;
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h; // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    out.assign(size_t(w) * size_t(h) * 4, 0);
    HDC dc = GetDC(nullptr);
    int lines = GetDIBits(dc, bmp, 0, static_cast<UINT>(h), out.data(), &bi, DIB_RGB_COLORS);
    ReleaseDC(nullptr, dc);
    return lines == h;
}

// Makes `px` premultiplied. A bitmap without any alpha is opaque. Where alpha
// exists, a colour channel above alpha cannot be premultiplied data, so the
// bitmap holds straight alpha and gets premultiplied here.
void MakePremultiplied(std::vector<uint8_t>& px)
{
    bool anyAlpha = false, straight = false;
    for (size_t i = 0; i < px.size(); i += 4) {
        uint8_t a = px[i + 3];
        anyAlpha |= a != 0;
        straight |= px[i] > a || px[i + 1] > a || px[i + 2] > a;
    }
    if (!anyAlpha) {
        for (size_t i = 3; i < px.size(); i += 4)
            px[i] = 255;
    } else if (straight) {
        for (size_t i = 0; i < px.size(); i += 4) {
            unsigned a = px[i + 3];
            for (size_t c = 0; c < 3; ++c)
                px[i + c] = static_cast<uint8_t>((px[i + c] * a + 127) / 255);
        }
    }
}

bool BitmapToImage(HBITMAP bmp, IconImage& out)
{
    if (!ReadBits(bmp, out.width, out.height, out.bgra))
        return false;
    MakePremultiplied(out.bgra);
    return true;
}

// IShellItemImageFactory on a parsing name (path, .lnk or shell:AppsFolder\...).
bool ShellImage(const std::wstring& parsingName, int size, IconImage& out, bool thumbnail)
{
    winrt::com_ptr<IShellItemImageFactory> factory;
    if (FAILED(SHCreateItemFromParsingName(parsingName.c_str(), nullptr, IID_PPV_ARGS(factory.put()))) || !factory)
        return false;
    const SIIGBF icons[] = {SIIGBF_ICONONLY, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, SIIGBF_BIGGERSIZEOK};
    const SIIGBF pictures[] = {SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, SIIGBF_RESIZETOFIT, SIIGBF_BIGGERSIZEOK};
    for (SIIGBF flags : thumbnail ? pictures : icons) {
        HBITMAP bmp = nullptr;
        if (SUCCEEDED(factory->GetImage(SIZE{size, size}, flags, &bmp)) && bmp) {
            IconImage raw;
            bool ok = BitmapToImage(bmp, raw);
            DeleteObject(bmp);
            if (ok) {
                out = FitToSquare(raw, size);
                return true;
            }
        }
    }
    return false;
}

// Monochrome icon: double-height mask (top half = AND, bottom half = XOR).
bool MonoIconToImage(HBITMAP maskBmp, IconImage& out)
{
    std::vector<uint8_t> mask;
    int mw = 0, mh = 0;
    if (!ReadBits(maskBmp, mw, mh, mask) || mw <= 0 || mh < 2 || (mh % 2) != 0)
        return false;
    const int h = mh / 2;
    const size_t plane = size_t(mw) * size_t(h) * 4;
    out.bgra.assign(plane, 0);
    for (size_t i = 0; i < plane; i += 4) {
        const bool andBit = (mask[i] | mask[i + 1] | mask[i + 2]) != 0;
        const bool xorBit = (mask[plane + i] | mask[plane + i + 1] | mask[plane + i + 2]) != 0;
        if (!andBit) {
            std::copy_n(&mask[plane + i], 3, &out.bgra[i]);
            out.bgra[i + 3] = 255;
        } else if (xorBit) {
            std::fill_n(&out.bgra[i], 4, uint8_t(255));
        }
    }
    out.width = mw;
    out.height = h;
    return true;
}

} // namespace

bool HIconToImage(HICON icon, IconImage& out)
{
    ICONINFO ii{};
    if (!GetIconInfo(icon, &ii))
        return false;
    bool ok = false;
    if (ii.hbmColor) {
        std::vector<uint8_t> color;
        int w = 0, h = 0;
        if (ReadBits(ii.hbmColor, w, h, color)) {
            bool hasAlpha = false;
            for (size_t i = 3; i < color.size() && !hasAlpha; i += 4)
                hasAlpha = color[i] != 0;
            if (hasAlpha) {
                MakePremultiplied(color);
            } else {
                // Old-style icon: transparency is in the AND mask (black = opaque).
                std::vector<uint8_t> mask;
                int mw = 0, mh = 0;
                bool haveMask = ii.hbmMask && ReadBits(ii.hbmMask, mw, mh, mask) && mw == w && mh == h;
                for (size_t i = 0; i < color.size(); i += 4) {
                    bool opaque = !haveMask || mask[i] == 0;
                    color[i + 3] = opaque ? 255 : 0;
                    if (!opaque)
                        color[i] = color[i + 1] = color[i + 2] = 0;
                }
            }
            out.width = w;
            out.height = h;
            out.bgra = std::move(color);
            ok = true;
        }
    } else if (ii.hbmMask) {
        ok = MonoIconToImage(ii.hbmMask, out);
    }
    if (ii.hbmColor)
        DeleteObject(ii.hbmColor);
    if (ii.hbmMask)
        DeleteObject(ii.hbmMask);
    return ok;
}

IconImage FitToSquare(const IconImage& src, int size)
{
    if (src.width == size && src.height == size)
        return src;
    IconImage out;
    out.width = out.height = size;
    out.bgra.assign(size_t(size) * size_t(size) * 4, 0);
    double scale = double(size) / double(std::max(src.width, src.height));
    int dw = std::max(1, int(std::lround(src.width * scale)));
    int dh = std::max(1, int(std::lround(src.height * scale)));
    int ox = (size - dw) / 2, oy = (size - dh) / 2;
    auto at = [&](int x, int y, int c) {
        x = std::clamp(x, 0, src.width - 1);
        y = std::clamp(y, 0, src.height - 1);
        return double(src.bgra[(size_t(y) * size_t(src.width) + size_t(x)) * 4 + size_t(c)]);
    };
    for (int dy = 0; dy < dh; ++dy) {
        double y0 = double(dy) * src.height / dh, y1 = double(dy + 1) * src.height / dh;
        for (int dx = 0; dx < dw; ++dx) {
            double x0 = double(dx) * src.width / dw, x1 = double(dx + 1) * src.width / dw;
            uint8_t* d = &out.bgra[(size_t(dy + oy) * size_t(size) + size_t(dx + ox)) * 4];
            for (int c = 0; c < 4; ++c) {
                double v = 0;
                if (x1 - x0 <= 1.0 && y1 - y0 <= 1.0) {
                    // Upscale: bilinear around the pixel centre.
                    double fx = (x0 + x1) / 2 - 0.5, fy = (y0 + y1) / 2 - 0.5;
                    int ix = int(std::floor(fx)), iy = int(std::floor(fy));
                    double tx = fx - ix, ty = fy - iy;
                    v = at(ix, iy, c) * (1 - tx) * (1 - ty) + at(ix + 1, iy, c) * tx * (1 - ty) + at(ix, iy + 1, c) * (1 - tx) * ty +
                        at(ix + 1, iy + 1, c) * tx * ty;
                } else {
                    // Downscale: area average with fractional edge weights.
                    double sum = 0, weight = 0;
                    for (int sy = int(std::floor(y0)); sy < int(std::ceil(y1)); ++sy) {
                        double wy = std::min<double>(sy + 1, y1) - std::max<double>(sy, y0);
                        for (int sx = int(std::floor(x0)); sx < int(std::ceil(x1)); ++sx) {
                            double wx = std::min<double>(sx + 1, x1) - std::max<double>(sx, x0);
                            sum += at(sx, sy, c) * wx * wy;
                            weight += wx * wy;
                        }
                    }
                    v = weight > 0 ? sum / weight : 0;
                }
                d[c] = static_cast<uint8_t>(std::clamp(int(std::lround(v)), 0, 255));
            }
        }
    }
    return out;
}

std::wstring AppExecutable(const std::wstring& path)
{
    std::wstring target = path;
    if (IsLink(path)) {
        winrt::com_ptr<IShellLinkW> link;
        if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(link.put()))))
            return {};
        auto file = link.try_as<IPersistFile>();
        wchar_t buf[MAX_PATH * 2] = {};
        if (!file || FAILED(file->Load(path.c_str(), STGM_READ)) || FAILED(link->GetPath(buf, ARRAYSIZE(buf), nullptr, 0)))
            return {};
        target = buf;
    }
    size_t slash = target.find_last_of(L"\\/");
    if (slash != std::wstring::npos) {
        std::wstring app = target.substr(0, slash + 1) + L"app\\" + target.substr(slash + 1);
        DWORD attr = GetFileAttributesW(app.c_str());
        if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY))
            return app;
    }
    return target;
}

bool LoadAtriumIcon(const std::wstring& exePath, int sizePx, IconImage& out)
{
    const size_t slash = exePath.find_last_of(L"\\/");
    const wchar_t* name = exePath.c_str() + (slash == std::wstring::npos ? 0 : slash + 1);
    if (_wcsicmp(name, L"Atrium.Settings.exe") != 0 && _wcsicmp(name, L"Atrium.exe") != 0)
        return false;
    HICON icon = nullptr;
    if (SHDefExtractIconW(exePath.c_str(), 0, 0, &icon, nullptr, MAKELONG(sizePx, 0)) != S_OK || !icon)
        return false;
    IconImage raw;
    bool ok = HIconToImage(icon, raw);
    DestroyIcon(icon);
    if (ok)
        out = FitToSquare(raw, sizePx);
    return ok;
}

bool LoadShellImage(const std::wstring& parsingName, int sizePx, IconImage& out, bool thumbnail)
{
    if (!thumbnail) {
        std::wstring source = AppExecutable(parsingName);
        int index = 0;
        if (IsLink(parsingName)) {
            winrt::com_ptr<IShellLinkW> link;
            if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(link.put())))) {
                if (auto file = link.try_as<IPersistFile>()) {
                    wchar_t iconPath[MAX_PATH * 2]{};
                    if (SUCCEEDED(file->Load(parsingName.c_str(), STGM_READ)) && SUCCEEDED(link->GetIconLocation(iconPath, ARRAYSIZE(iconPath), &index)) &&
                        *iconPath)
                        source = iconPath;
                }
            }
        }
        HICON icon = nullptr;
        if (!source.empty() && SHDefExtractIconW(source.c_str(), index, 0, &icon, nullptr, MAKELONG(sizePx, 0)) == S_OK && icon) {
            IconImage raw;
            bool ok = HIconToImage(icon, raw);
            DestroyIcon(icon);
            if (ok) {
                out = FitToSquare(raw, sizePx);
                return true;
            }
        }
    }
    return ShellImage(parsingName, sizePx, out, thumbnail);
}

} // namespace atrium
