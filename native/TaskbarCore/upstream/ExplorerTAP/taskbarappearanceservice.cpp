#include "taskbarappearanceservice.hpp"
#include <RpcProxy.h>
#include <CommCtrl.h>
#include <cstdint>
#include <memory>
#include <shellapi.h>
#include <wil/cppwinrt_helpers.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Management.Deployment.h>
#include <winrt/Windows.UI.Xaml.Controls.h>

#include "constants.hpp"
#include "util/color.hpp"

#include "XamlBlurBrush.h"
#include "effects/RefractedBackdrop.h"

extern "C"
{
	_Check_return_ HRESULT STDAPICALLTYPE DLLGETCLASSOBJECT_ENTRY(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ void** ppv);
}

namespace
{
	constexpr wchar_t CapsuleOwner[] = L"DesktopNook.NativeTaskbarShape.Owner";
	constexpr wchar_t CapsulePosition[] = L"DesktopNook.NativeTaskbarShape.Position";
	constexpr wchar_t CapsuleGeometryLock[] = L"DesktopNook.NativeTaskbarShape.GeometryLock";
	constexpr wchar_t CapsuleInset[] = L"DesktopNook.NativeTaskbarShape.Inset";
	constexpr wchar_t CapsuleDivider[] = L"DesktopNook.NativeTaskbarShape.Divider";
	constexpr wchar_t CapsuleTrim[] = L"DesktopNook.NativeTaskbarShape.Trim";
	constexpr wchar_t CapsuleTrimApplied[] = L"DesktopNook.NativeTaskbarShape.TrimApplied";
	constexpr wchar_t CapsuleSmooth[] = L"DesktopNook.NativeTaskbarShape.Smooth";
	constexpr wchar_t CapsuleDividerVisible[] = L"DesktopNook.NativeTaskbarShape.DividerVisible";
	constexpr UINT_PTR CapsuleSubclassId = 1;
	const UINT CapsuleVisualRefresh = RegisterWindowMessageW(L"DesktopNook.NativeTaskbarShape.VisualRefresh");
	constexpr UINT_PTR PreviewSubclassId = 0x5a505256;
	constexpr wchar_t PreviewBridge[] = L"DesktopNook.Preview.Bridge";
	constexpr wchar_t PreviewOrigin[] = L"DesktopNook.Preview.Origin";
	constexpr wchar_t PreviewX[] = L"DesktopNook.Preview.X";
	constexpr wchar_t PreviewY[] = L"DesktopNook.Preview.Y";
	constexpr wchar_t PreviewAnchorX[] = L"DesktopNook.Preview.AnchorX";

	bool PreviewButton(HWND bridge, RECT& button)
	{
		POINT pointer{};
		if (!GetCursorPos(&pointer)) return false;
		const auto count = reinterpret_cast<INT_PTR>(GetPropW(bridge, L"DesktopNook.Capsule.ButtonCount"));
		for (INT_PTR index = 0; index < count && index < 128; ++index)
		{
			wchar_t key[96]{};
			swprintf_s(key, L"DesktopNook.Capsule.Button.%lld.Position", static_cast<long long>(index));
			const auto origin = reinterpret_cast<std::uintptr_t>(GetPropW(bridge, key));
			swprintf_s(key, L"DesktopNook.Capsule.Button.%lld.Size", static_cast<long long>(index));
			const auto size = reinterpret_cast<std::uintptr_t>(GetPropW(bridge, key));
			RECT candidate{static_cast<LONG>(origin), static_cast<LONG>(origin >> 32), 0, 0};
			candidate.right = candidate.left + static_cast<LONG>(size);
			candidate.bottom = candidate.top + static_cast<LONG>(size >> 32);
			if (PtInRect(&candidate, pointer)) { button = candidate; return true; }
		}
		return false;
	}

	bool PreviewScreenOrigin(HWND bridge, POINT& origin)
	{
		if (!bridge || !GetPropW(bridge, CapsuleOwner)) return false;
		RECT child{}, taskbar{};
		MONITORINFO monitor{sizeof(monitor)};
		if (!GetWindowRect(bridge, &child) || !GetWindowRect(GetParent(bridge), &taskbar) ||
			!GetMonitorInfoW(MonitorFromWindow(bridge, MONITOR_DEFAULTTONEAREST), &monitor)) return false;
		origin = {monitor.rcMonitor.left + child.left - taskbar.left,
			monitor.rcMonitor.top + child.top - taskbar.top};
		return true;
	}

	LRESULT CALLBACK KeepPreviewPosition(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR)
	{
		if (message == WM_WINDOWPOSCHANGING && l)
		{
			auto result = DefSubclassProc(window, message, w, l);
			const auto bridge = static_cast<HWND>(GetPropW(window, PreviewBridge));
			POINT origin{};
			if (PreviewScreenOrigin(bridge, origin))
			{
				if (const auto saved = reinterpret_cast<INT_PTR>(GetPropW(window, PreviewX)))
				{
					origin.x = static_cast<int>(saved) - 1000000;
					origin.y = static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(window, PreviewY))) - 1000000;
					RECT button{};
					const auto previous = reinterpret_cast<INT_PTR>(GetPropW(window, PreviewAnchorX));
					if (previous && PreviewButton(bridge, button))
					{
						const int center = (button.left + button.right) / 2;
						origin.x += center - (static_cast<int>(previous) - 1000000);
						SetPropW(window, PreviewX, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(origin.x + 1000000)));
						SetPropW(window, PreviewAnchorX, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(center + 1000000)));
					}
				}
				auto& position = *reinterpret_cast<WINDOWPOS*>(l);
				position.x = origin.x;
				position.y = origin.y;
				position.flags &= ~SWP_NOMOVE;
			}
			return result;
		}
		if (message == WM_NCDESTROY)
		{
			RemoveWindowSubclass(window, KeepPreviewPosition, id);
			RemovePropW(window, PreviewOrigin);
			RemovePropW(window, PreviewBridge);
			RemovePropW(window, PreviewX); RemovePropW(window, PreviewY); RemovePropW(window, PreviewAnchorX);
		}
		return DefSubclassProc(window, message, w, l);
	}

	void RestorePreviewHost(HWND window)
	{
		if (!window || !GetPropW(window, PreviewBridge)) return;
		RemoveWindowSubclass(window, KeepPreviewPosition, PreviewSubclassId);
		const auto packed = reinterpret_cast<std::uintptr_t>(RemovePropW(window, PreviewOrigin));
		RemovePropW(window, PreviewBridge);
		RemovePropW(window, PreviewX); RemovePropW(window, PreviewY); RemovePropW(window, PreviewAnchorX);
		SetWindowPos(window, nullptr, static_cast<int>(packed), static_cast<int>(packed >> 32), 0, 0,
			SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
	}

	struct CapsuleVisual
	{
		wux::UIElement Content{ nullptr };
		winrt::weak_ref<TaskbarAppearanceService> Owner;
		wuc::Visual Visual{ nullptr };
		wuc::CompositionClip OriginalClip{ nullptr };
		wuc::CompositionGeometricClip Clip{ nullptr };
		wuc::CompositionRoundedRectangleGeometry Outline{ nullptr };
		wuc::ContainerVisual DividerParent{ nullptr };
		wuc::SpriteVisual Divider{ nullptr };
		wux::UIElement GlassBackground{ nullptr };
		wuc::ShapeVisual GlassEdges{ nullptr };
		wuc::CompositionRoundedRectangleGeometry GlassOutline{ nullptr };
		wuc::CompositionRoundedRectangleGeometry RefractionOutline{ nullptr };
		wux::UIElement Tray{ nullptr };
		wux::Media::Transform OriginalTrayTransform{ nullptr };
		wux::Media::Transform TrayTransform{ nullptr };
		bool OwnsContainer = false;
		int Width = 0, Height = 0, Inset = -1;
		int Trim = 0;
		UINT Dpi = 0;

		void SizeGlass(HWND window)
		{
			RECT rect{};
			if (!GlassEdges || !GetClientRect(window, &rect)) return;
			const bool capsule = GetPropW(window, CapsuleOwner) && GetPropW(window, CapsulePosition);
			const float pixel = 96.f / static_cast<float>(GetDpiForWindow(window));
			const float width = (rect.right - (capsule ? Trim : 0)) * pixel;
			const float height = rect.bottom * pixel;
			if (width < height || height < 8.f) return;
			GlassEdges.Size({width, height});
			auto sizeOutline = [&](auto const& geometry, float stroke)
			{
				float radius = capsule ? (height - stroke) / 2.f : 0.f;
				geometry.Offset({stroke / 2, stroke / 2});
				geometry.Size({width - stroke, height - stroke});
				geometry.CornerRadius({radius,radius});
			};
			sizeOutline(GlassOutline, 3.f);
			if (RefractionOutline) sizeOutline(RefractionOutline, 6.f);
		}

		void SetGlass(HWND window, const wux::UIElement& background)
		{
			if (GlassBackground == background && GlassEdges) { SizeGlass(window); return; }
			if (GlassBackground && wuxh::ElementCompositionPreview::GetElementChildVisual(GlassBackground) == GlassEdges)
				wuxh::ElementCompositionPreview::SetElementChildVisual(GlassBackground, nullptr);
			GlassBackground = nullptr; GlassEdges = nullptr; GlassOutline = nullptr; RefractionOutline = nullptr;
			if (!background || wuxh::ElementCompositionPreview::GetElementChildVisual(background)) return;
			auto compositor = wuxh::ElementCompositionPreview::GetElementVisual(background).Compositor();
			GlassEdges = compositor.CreateShapeVisual();
			auto stroke = [&](auto const& geometry, auto const& brush, float thickness)
			{
				auto line = compositor.CreateSpriteShape(geometry);
				line.StrokeThickness(thickness);
				line.StrokeBrush(brush);
				GlassEdges.Shapes().Append(line);
			};
			try
			{
				auto offset = winrt::make_self<RefractedBackdrop>();
				offset->Source = wuc::CompositionEffectSourceParameter(L"backdrop");
				auto brush = compositor.CreateEffectFactory(*offset).CreateBrush();
				brush.SetSourceParameter(L"backdrop", compositor.CreateBackdropBrush());
				RefractionOutline = compositor.CreateRoundedRectangleGeometry();
				stroke(RefractionOutline, brush, 6.f);
			}
			catch (const winrt::hresult_error&) { RefractionOutline = nullptr; }
			auto light = compositor.CreateLinearGradientBrush();
			light.StartPoint({0,0}); light.EndPoint({0,1});
			light.ColorStops().Append(compositor.CreateColorGradientStop(0.f,{190,255,255,255}));
			light.ColorStops().Append(compositor.CreateColorGradientStop(.45f,{32,255,255,255}));
			light.ColorStops().Append(compositor.CreateColorGradientStop(1.f,{80,230,245,255}));
			GlassOutline = compositor.CreateRoundedRectangleGeometry();
			stroke(GlassOutline, light, 3.f);
			GlassBackground = background;
			SizeGlass(window);
			wuxh::ElementCompositionPreview::SetElementChildVisual(background, GlassEdges);
		}

		static wux::UIElement FindTray(const wux::DependencyObject& parent, int depth)
		{
			if (!parent || depth < 0) return nullptr;
			const auto type = winrt::get_class_name(parent);
			if (type == L"SystemTray.SystemTrayFrame")
				return parent.try_as<wux::UIElement>();
			// Newer Explorer builds host the tray as the content of a repeater
			// item. Move the item so its complete hit surface moves with the tray.
			if (type == L"Taskbar.TaskbarExtensionElement")
			{
				if (const auto host = parent.try_as<wux::Controls::ContentControl>())
					if (const auto tray = host.Content();
						tray && winrt::get_class_name(tray) == L"SystemTray.SystemTrayFrame")
						return host;
			}
			const int count = wux::Media::VisualTreeHelper::GetChildrenCount(parent);
			for (int i = 0; i < count; ++i)
				if (auto tray = FindTray(wux::Media::VisualTreeHelper::GetChild(parent, i), depth - 1))
					return tray;
			return nullptr;
		}

		void ResetTray()
		{
			if (Tray && TrayTransform && Tray.RenderTransform() == TrayTransform)
				Tray.RenderTransform(OriginalTrayTransform);
			Tray = nullptr;
			OriginalTrayTransform = nullptr;
			TrayTransform = nullptr;
		}

		int ApplyTrayTrim(int trim, float pixel, UINT dpi)
		{
			if (trim <= 0)
			{
				ResetTray();
				return 0;
			}
			if (!Tray || !Tray.XamlRoot())
			{
				ResetTray();
				if (const auto root = Content.XamlRoot())
					Tray = FindTray(root.Content(), 6);
				if (!Tray) return 0;
			}
			if (TrayTransform && Tray.RenderTransform() == TrayTransform && Trim == trim && Dpi == dpi)
				return trim;
			if (!TrayTransform || Tray.RenderTransform() != TrayTransform)
				OriginalTrayTransform = Tray.RenderTransform();
			wux::Media::TranslateTransform offset;
			offset.X(-trim * pixel);
			if (OriginalTrayTransform)
			{
				wux::Media::TransformGroup transforms;
				transforms.Children().Append(OriginalTrayTransform);
				transforms.Children().Append(offset);
				TrayTransform = transforms;
			}
			else
				TrayTransform = offset;
			Tray.RenderTransform(TrayTransform);
			return trim;
		}

		void Reset(HWND window)
		{
			RemovePropW(window, CapsuleSmooth);
			RemovePropW(window, CapsuleDividerVisible);
			RemovePropW(window, CapsuleTrimApplied);
			ResetTray();
			if (Visual && Clip && Visual.Clip() == Clip)
				Visual.Clip(OriginalClip);
			if (DividerParent && Divider)
				DividerParent.Children().Remove(Divider);
			if (OwnsContainer && Content && wuxh::ElementCompositionPreview::GetElementChildVisual(Content) == DividerParent)
				wuxh::ElementCompositionPreview::SetElementChildVisual(Content, nullptr);
			Visual = nullptr;
			OriginalClip = nullptr;
			Clip = nullptr;
			Outline = nullptr;
			DividerParent = nullptr;
			Divider = nullptr;
			OwnsContainer = false;
			Inset = -1;
			Trim = 0;
		}

		void Refresh(HWND window)
		{
			const auto packed = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsulePosition));
			const auto insetValue = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleInset));
			if (!Content || !packed || !insetValue || !GetPropW(window, CapsuleOwner))
			{
				Reset(window);
				SizeGlass(window);
				return;
			}
			const int inset = static_cast<int>(insetValue) - 1;
			const int width = static_cast<int>((packed >> 32) & 0xffff);
			const int height = static_cast<int>((packed >> 48) & 0xffff);
			const int visible = height - inset * 2;
			const UINT dpi = GetDpiForWindow(window);
			if (width < 100 || visible < 20 || inset < 0 || dpi == 0) return;

			if (!Outline)
			{
				Visual = wuxh::ElementCompositionPreview::GetElementVisual(Content);
				OriginalClip = Visual.Clip();
				const auto compositor = Visual.Compositor();
				Outline = compositor.CreateRoundedRectangleGeometry();
				Clip = compositor.CreateGeometricClip(Outline);
				if (const auto current = wuxh::ElementCompositionPreview::GetElementChildVisual(Content))
					DividerParent = current.try_as<wuc::ContainerVisual>();
				else
				{
					DividerParent = compositor.CreateContainerVisual();
					wuxh::ElementCompositionPreview::SetElementChildVisual(Content, DividerParent);
					OwnsContainer = true;
				}
				if (DividerParent)
				{
					Divider = compositor.CreateSpriteVisual();
					Divider.Brush(compositor.CreateColorBrush({ 155, 100, 112, 128 }));
					DividerParent.Children().InsertAtTop(Divider);
				}
			}

			const float pixel = 96.f / static_cast<float>(dpi);
			const int requested = static_cast<int>(reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleTrim)));
			const int trim = ApplyTrayTrim(requested > 0 && requested <= width / 3 ? requested : 0, pixel, dpi);
			const int faceWidth = width - trim;
			const float radius = static_cast<float>(visible) / 2 * pixel;
			if (Width != width || Height != height || Inset != inset || Trim != trim || Dpi != dpi || Visual.Clip() != Clip)
			{
				Outline.Offset({ 0, inset * pixel });
				Outline.Size({ faceWidth * pixel, visible * pixel });
				Outline.CornerRadius({ radius, radius });
				Visual.Clip(Clip);
				// A GDI rounded region clips off the antialiased pixels. Keep only a
				// rectangular Win32 region for the vertical insets; Composition owns the curve.
				if (const auto region = CreateRectRgn(0, inset, faceWidth, height - inset))
				{
					if (!SetWindowRgn(window, region, TRUE))
						DeleteObject(region);
					else
						SetPropW(window, CapsuleSmooth, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1)));
				}
				Width = width;
				Height = height;
				Inset = inset;
				Trim = trim;
				Dpi = dpi;
			}
			if (trim)
				SetPropW(window, CapsuleTrimApplied, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(trim)));
			else
				RemovePropW(window, CapsuleTrimApplied);
			SizeGlass(window);
			if (Divider)
			{
				const auto x = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleDivider));
				const bool draw = x > 2 && x < static_cast<std::uintptr_t>(faceWidth - 2);
				Divider.Opacity(draw ? 1.f : 0.f);
				Divider.Size({ 2.f * pixel, visible * .62f * pixel });
				Divider.Offset({ static_cast<float>(x) * pixel, (inset + visible * .19f) * pixel, 0 });
				if (draw)
					SetPropW(window, CapsuleDividerVisible, reinterpret_cast<HANDLE>(x));
				else
					RemovePropW(window, CapsuleDividerVisible);
			}
		}
	};

	LRESULT CALLBACK KeepCapsulePosition(HWND window, UINT message, WPARAM wParam, LPARAM lParam,
		UINT_PTR subclassId, DWORD_PTR context)
	{
		auto* capsule = reinterpret_cast<CapsuleVisual*>(context);
		if (message == CapsuleVisualRefresh && CapsuleVisualRefresh)
		{
			try { capsule->Refresh(window); }
			catch (...) {} // Keep the rounded Win32 fallback if the compositor cannot create the clip.
			if (const auto owner = capsule->Owner.get()) owner->RefreshPreviews();
			return 0;
		}
		if (message == WM_WINDOWPOSCHANGING && lParam)
		{
			// Let Explorer perform its normal validation first, then retain the managed
			// capsule coordinates even if its default handler proposed a full-width child.
			const auto result = DefSubclassProc(window, message, wParam, lParam);
			const auto packed = GetPropW(window, CapsuleOwner)
				? reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsulePosition)) : 0;
			auto& position = *reinterpret_cast<WINDOWPOS*>(lParam);
			if (packed && (position.flags & (SWP_NOMOVE | SWP_NOSIZE)) != (SWP_NOMOVE | SWP_NOSIZE))
			{
				const auto x = static_cast<int>(packed & 0xffff);
				const auto y = static_cast<std::int16_t>((packed >> 16) & 0xffff);
				const auto width = static_cast<int>((packed >> 32) & 0xffff);
				const auto height = static_cast<int>((packed >> 48) & 0xffff);
				RECT parent{};
				if (GetClientRect(GetParent(window), &parent) && width > 0 && x + width <= parent.right &&
					height == parent.bottom && y >= -16 && y <= 0)
				{
					position.x = x;
					position.y = y;
					position.cx = width;
					position.cy = height;
					position.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
				}
			}
			return result;
		}
		else if (message == WM_NCDESTROY)
		{
			RemovePropW(window, CapsuleGeometryLock);
			RemoveWindowSubclass(window, KeepCapsulePosition, subclassId);
			delete capsule;
		}
		return DefSubclassProc(window, message, wParam, lParam);
	}

	void DetachCapsulePositionLock(HWND window)
	{
		DWORD_PTR context = 0;
		if (GetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, &context) &&
			RemoveWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId))
		{
			RemovePropW(window, CapsuleGeometryLock);
			std::unique_ptr<CapsuleVisual> capsule(reinterpret_cast<CapsuleVisual*>(context));
			try { capsule->SetGlass(window, nullptr); capsule->Reset(window); } catch (...) {}
		}
	}

	void SetGlassEdges(HWND window, const wux::UIElement& background)
	{
		DWORD_PTR context{};
		if (GetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, &context))
			reinterpret_cast<CapsuleVisual*>(context)->SetGlass(window, background);
	}
}

DWORD TaskbarAppearanceService::s_ProxyStubRegistrationCookie = 0;

void TaskbarAppearanceService::RestorePreview(PreviewInfo& preview)
{
	if (preview.positionedHost)
	{
		auto bridge = static_cast<HWND>(GetPropW(preview.positionedHost, PreviewBridge));
		if (!bridge || !GetPropW(bridge, CapsuleOwner)) RestorePreviewHost(preview.positionedHost);
	}
	preview.positionedHost = nullptr;
}

void TaskbarAppearanceService::RegisterPreview(InstanceHandle handle, const wux::FrameworkElement& element, HWND window)
{
	if (const auto found = m_Previews.find(handle); found != m_Previews.end())
	{
		if (window) found->second.window = window;
		try { AlignPreview(handle); } catch (const winrt::hresult_error&) {}
		return;
	}
	PreviewInfo preview;
	preview.element = element;
	preview.window = window;
	preview.changed = element.LayoutUpdated([weak = get_weak(), handle](const auto&, const auto&)
	{
		if (const auto self = weak.get())
			try { self->AlignPreview(handle); } catch (const winrt::hresult_error&) {}
	});
	m_Previews.emplace(handle, std::move(preview));
	try { AlignPreview(handle); } catch (const winrt::hresult_error&) {}
}

void TaskbarAppearanceService::UnregisterPreview(InstanceHandle handle)
{
	if (const auto found = m_Previews.find(handle); found != m_Previews.end())
	{
		try
		{
			found->second.element.LayoutUpdated(found->second.changed);
			RestorePreview(found->second);
		}
		catch (const winrt::hresult_error&) {}
		m_Previews.erase(found);
	}
}

void TaskbarAppearanceService::RefreshPreviews()
{
	for (auto host = m_PreviewHosts.begin(); host != m_PreviewHosts.end();)
	{
		auto bridge = static_cast<HWND>(GetPropW(*host, PreviewBridge));
		if (!IsWindow(*host) || !bridge || !GetPropW(bridge, CapsuleOwner))
		{
			RestorePreviewHost(*host);
			host = m_PreviewHosts.erase(host);
		}
		else ++host;
	}
	for (const auto& [frame, info] : m_Taskbars) RemovePropW(info.window, L"DesktopNook.Capsule.PreviewAligned");
	for (const auto& [handle, preview] : m_Previews)
		try { AlignPreview(handle); } catch (const winrt::hresult_error&) {}
}

void TaskbarAppearanceService::AlignPreview(InstanceHandle handle)
{
    auto& preview = m_Previews.at(handle);
    HWND host = GetAncestor(preview.window, GA_ROOT);
    wchar_t hostClass[128]{};
    if (!host || !GetClassNameW(host, hostClass, ARRAYSIZE(hostClass)) ||
        std::wstring_view(hostClass) != L"XamlExplorerHostIslandWindow")
    {
        host = nullptr;
        HWND candidate{};
        while ((candidate = FindWindowExW(nullptr, candidate, L"XamlExplorerHostIslandWindow", nullptr)))
            if (GetWindowThreadProcessId(candidate, nullptr) == GetCurrentThreadId()) { host = candidate; break; }
    }
    if (!host) return;
    for (const auto& [frame, info] : m_Taskbars)
    {
        POINT target{};
        if (!PreviewScreenOrigin(info.window, target)) continue;
        if (MonitorFromWindow(info.window, MONITOR_DEFAULTTONEAREST) != MonitorFromWindow(host, MONITOR_DEFAULTTONEAREST)) continue;
        RECT hostBounds{};
        if (!GetWindowRect(host, &hostBounds)) continue;
        const auto root = preview.element.XamlRoot();
        RECT button{};
        RECT placed{};
        bool positioned = false;
        if (root && preview.element.ActualWidth() > 10 && preview.element.ActualHeight() > 10 && PreviewButton(info.window, button))
        {
            const auto bounds = preview.element.TransformToVisual(root.Content()).TransformBounds(
                {0, 0, static_cast<float>(preview.element.ActualWidth()), static_cast<float>(preview.element.ActualHeight())});
            const float scale = static_cast<float>(GetDpiForWindow(info.window)) / 96.f;
            POINT clientOrigin{};
            if (!ClientToScreen(host, &clientOrigin)) continue;
            const POINT anchor{(button.left + button.right) / 2, button.top - static_cast<int>(5 * scale)};
            const SIZE dimensions{static_cast<LONG>(bounds.Width * scale), static_cast<LONG>(bounds.Height * scale)};
            if (CalculatePopupWindowPosition(&anchor, &dimensions, TPM_CENTERALIGN | TPM_BOTTOMALIGN | TPM_VERTICAL | TPM_WORKAREA,
                &button, &placed))
            {
                target.x = hostBounds.left + placed.left - clientOrigin.x - static_cast<int>(bounds.X * scale);
                target.y = hostBounds.top + placed.top - clientOrigin.y - static_cast<int>(bounds.Y * scale);
                positioned = true;
                SetPropW(host, PreviewAnchorX, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(anchor.x + 1000000)));
            }
        }
        if (!positioned && GetPropW(host, PreviewX)) continue;
        SetPropW(host, PreviewX, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(target.x + 1000000)));
        SetPropW(host, PreviewY, reinterpret_cast<HANDLE>(static_cast<INT_PTR>(target.y + 1000000)));
        if (!GetPropW(host, PreviewBridge))
        {
            const auto original = static_cast<std::uint64_t>(static_cast<std::uint32_t>(hostBounds.left)) |
                (static_cast<std::uint64_t>(static_cast<std::uint32_t>(hostBounds.top)) << 32);
            if (!SetPropW(host, PreviewOrigin, reinterpret_cast<HANDLE>(original)) ||
                !SetPropW(host, PreviewBridge, info.window) || !SetWindowSubclass(host, KeepPreviewPosition, PreviewSubclassId, 0))
            { RestorePreviewHost(host); continue; }
        }
        preview.positionedHost = host;
        m_PreviewHosts.insert(host);
        if (hostBounds.left != target.x || hostBounds.top != target.y)
            SetWindowPos(host, nullptr, target.x, target.y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        if (positioned)
        {
            SetPropW(info.window, L"DesktopNook.Capsule.PreviewCardX", reinterpret_cast<HANDLE>(static_cast<INT_PTR>(placed.left + 1000000)));
            SetPropW(info.window, L"DesktopNook.Capsule.PreviewCardY", reinterpret_cast<HANDLE>(static_cast<INT_PTR>(placed.top + 1000000)));
            SetPropW(info.window, L"DesktopNook.Capsule.PreviewOffsetX", reinterpret_cast<HANDLE>(1000000));
            SetPropW(info.window, L"DesktopNook.Capsule.PreviewOffsetY", reinterpret_cast<HANDLE>(1000000));
        }
        SetPropW(info.window, L"DesktopNook.Capsule.PreviewAligned", host);
        return;
    }
}
TaskbarAppearanceService::TaskbarAppearanceService() :
	m_RegisterCookie(0),
	m_XamlThreadQueue(winrt::Windows::System::DispatcherQueue::GetForCurrentThread())
{
	InstallProxyStub();
	winrt::check_hresult(RegisterActiveObject(static_cast<ITaskbarAppearanceService*>(this), CLSID_TaskbarAppearanceService, ACTIVEOBJECT_STRONG, &m_RegisterCookie));
}

HRESULT TaskbarAppearanceService::GetVersion(DWORD* apiVersion) noexcept
{
	if (apiVersion)
	{
		*apiVersion = TAP_API_VERSION;
	}

	return S_OK;
}

HRESULT TaskbarAppearanceService::SetTaskbarAppearance(HWND taskbar, TaskbarBrush brush, UINT color) try
{
	if (const auto info = GetTaskbarInfo(taskbar))
	{
		if (info->background.control && info->background.originalFill)
		{
			const winrt::Windows::UI::Color tint = Util::Color::FromABGR(color);
			wux::Media::Brush newBrush = nullptr;
			if (brush == Acrylic)
			{
				wux::Media::AcrylicBrush acrylicBrush;
				// on the taskbar, using Backdrop instead of HostBackdrop
				// makes the effect still show what's behind, but also not disable itself
				// when the window isn't active
				// this is because it sources what's behind the XAML, and the taskbar window
				// is transparent so what's behind is actually the content behind the window
				// (it doesn't need to poke a hole like HostBackdrop)
				acrylicBrush.BackgroundSource(wux::Media::AcrylicBackgroundSource::Backdrop);
				acrylicBrush.TintColor(tint);

				newBrush = std::move(acrylicBrush);
			}
			else if (brush == LiquidGlass)
			{
				auto compositor = wuxh::ElementCompositionPreview::GetElementVisual(info->background.control).Compositor();
				const wfn::float4 rgba{ tint.R / 255.f, tint.G / 255.f, tint.B / 255.f, tint.A / 255.f };
				newBrush = winrt::make<XamlBlurBrush>(compositor, 2.f, rgba);
			}
			else if (brush == SolidColor)
			{
				wux::Media::SolidColorBrush solidBrush;
				solidBrush.Color(tint);

				newBrush = std::move(solidBrush);
			}

			info->background.control.Fill(newBrush);
			SetGlassEdges(info->window, brush == LiquidGlass ? info->background.control : nullptr);
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::SetTaskbarBlur(HWND taskbar, UINT color, FLOAT blurAmount) try
{
	if (const auto info = GetTaskbarInfo(taskbar))
	{
		if (info->background.control && info->background.originalFill)
		{
			const winrt::Windows::UI::Color tint = Util::Color::FromABGR(color);
			const wfn::float4 tintHdr = {
				tint.R / 255.0f,
				tint.G / 255.0f,
				tint.B / 255.0f,
				tint.A / 255.0f
			};

			auto compositor = wuxh::ElementCompositionPreview::GetElementVisual(info->background.control).Compositor();
			info->background.control.Fill(winrt::make<XamlBlurBrush>(std::move(compositor), blurAmount, tintHdr));
			SetGlassEdges(info->window, nullptr);
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::ReturnTaskbarToDefaultAppearance(HWND taskbar) try
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			RestoreDefaultControlFill(info.background);
			SetGlassEdges(info.window, nullptr);
			break;
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::SetTaskbarBorderVisibility(HWND taskbar, BOOL visible) try
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			if (visible)
			{
				RestoreDefaultControlFill(info.border);
			}
			else
			{
				if (info.border.control && info.border.originalFill)
				{
					wux::Media::SolidColorBrush brush;
					brush.Opacity(0);
					info.border.control.Fill(brush);
				}
			}

			break;
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::RestoreAllTaskbarsToDefault() try
{
	for (HWND host : m_PreviewHosts) RestorePreviewHost(host);
	m_PreviewHosts.clear();
	for (auto& [handle, preview] : m_Previews) RestorePreview(preview);
	for (const auto& [handle, info] : m_Taskbars)
	{
		RestoreDefaultControlFill(info.background);
		RestoreDefaultControlFill(info.border);
		SetGlassEdges(info.window, nullptr);
		RemovePropW(info.window, L"DesktopNook.Capsule.PreviewAligned");
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::RestoreAllTaskbarsToDefaultWhenProcessDies(DWORD pid)
{
	if (!m_Process)
	{
		m_Process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid));
		if (!m_Process) [[unlikely]]
		{
			return HRESULT_FROM_WIN32(GetLastError());
		}

		if (!RegisterWaitForSingleObject(m_WaitHandle.put(), m_Process.get(), ProcessWaitCallback, this, INFINITE, WT_EXECUTEONLYONCE))
		{
			m_Process.reset();
			return HRESULT_FROM_WIN32(GetLastError());
		}

		return S_OK;
	}
	else
	{
		if (GetProcessId(m_Process.get()) == pid)
		{
			return S_OK; // already watching for this process to die.
		}
		else
		{
			return E_ILLEGAL_METHOD_CALL;
		}
	}
}

HRESULT TaskbarAppearanceService::KillExplorerWhenPackageUninstalls(LPCWSTR packageFullName) try
{
	const std::wstring_view packageFullNameSv { packageFullName };

	if (!m_PackageCatalog)
	{
		winrt::Windows::Management::Deployment::PackageManager pm;
		auto catalog = wam::PackageCatalog::OpenForPackage(pm.FindPackageForUser(L"", packageFullNameSv));

		m_PackageUninstallingToken = catalog.PackageUninstalling({ get_weak(), &TaskbarAppearanceService::OnPackageUninstalling });
		m_PackageUpdatingToken = catalog.PackageUpdating({ get_weak(), &TaskbarAppearanceService::OnPackageUpdating });
		m_PackageBeingWatched = packageFullNameSv;
		m_PackageCatalog = std::move(catalog);

		return S_OK;
	}
	else
	{
		if (m_PackageBeingWatched == packageFullNameSv)
		{
			return S_OK; // already watching for this package to get uninstalled.
		}
		else
		{
			return E_ILLEGAL_METHOD_CALL;
		}
	}
}
catch (...)
{
	return winrt::to_hresult();
}

void TaskbarAppearanceService::RegisterTaskbar(InstanceHandle frameHandle, HWND window, const wux::UIElement& content)
{
	m_Taskbars.insert_or_assign(frameHandle, TaskbarInfo { { }, { }, window });
	DWORD_PTR existing = 0;
	if (GetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, &existing))
		return;
	auto capsule = std::make_unique<CapsuleVisual>();
	capsule->Content = content;
	capsule->Owner = get_weak();
	if (!SetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, reinterpret_cast<DWORD_PTR>(capsule.get())))
		return;
	if (!SetPropW(window, CapsuleGeometryLock, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1))))
		RemoveWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId);
	else
		static_cast<void>(capsule.release());
}

void TaskbarAppearanceService::RegisterTaskbarBackground(InstanceHandle frameHandle, wux::Shapes::Shape element)
{
	if (const auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		it->second.background.control = element;

		// sometimes we may see objects come with their fill set to null, wait until the system initialized it before
		// we start messing with it
		// in the future if the taskbar doesn't change the original brush, but tries to change the entire brush
		// this could be useful to suppress it and update the saved brush
		element.RegisterPropertyChangedCallback(wux::Shapes::Shape::FillProperty(), { get_weak(), &TaskbarAppearanceService::OnTaskbarBackgroundUpdated });
		it->second.background.originalFill = element.Fill();
	}
}

void TaskbarAppearanceService::RegisterTaskbarBorder(InstanceHandle frameHandle, wux::Shapes::Shape element)
{
	if (const auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		it->second.border.control = element;

		element.RegisterPropertyChangedCallback(wux::Shapes::Shape::FillProperty(), { get_weak(), &TaskbarAppearanceService::OnTaskbarBorderUpdated });
		it->second.border.originalFill = element.Fill();
	}
}

void TaskbarAppearanceService::UnregisterTaskbar(InstanceHandle frameHandle)
{
	if (auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		DetachCapsulePositionLock(it->second.window);
		const auto fillProperty = wux::Shapes::Shape::FillProperty();
		if (it->second.background.fillChangedToken != 0)
		{
			it->second.background.control.UnregisterPropertyChangedCallback(fillProperty, it->second.background.fillChangedToken);
		}

		if (it->second.border.fillChangedToken != 0)
		{
			it->second.border.control.UnregisterPropertyChangedCallback(fillProperty, it->second.border.fillChangedToken);
		}

		m_Taskbars.erase(it);
	}
}

TaskbarAppearanceService::~TaskbarAppearanceService()
{
	for (HWND host : m_PreviewHosts) RestorePreviewHost(host);
	m_PreviewHosts.clear();
	while (!m_Previews.empty()) UnregisterPreview(m_Previews.begin()->first);
	if (m_PackageCatalog)
	{
		m_PackageCatalog.PackageUpdating(m_PackageUpdatingToken);
		m_PackageCatalog.PackageUninstalling(m_PackageUninstallingToken);

		m_PackageBeingWatched.clear();
		m_PackageUpdatingToken = { };
		m_PackageUninstallingToken = { };
		m_PackageCatalog = nullptr;
	}

	// wait for all callbacks to be done, as they have a raw pointer to the instance.
	winrt::check_bool(UnregisterWaitEx(m_WaitHandle.get(), INVALID_HANDLE_VALUE));
	m_WaitHandle.release();

	m_Process.reset();

	winrt::check_hresult(RestoreAllTaskbarsToDefault());
	winrt::check_hresult(RevokeActiveObject(m_RegisterCookie, nullptr));
}

void TaskbarAppearanceService::InstallProxyStub()
{
	if (!s_ProxyStubRegistrationCookie)
	{
		winrt::com_ptr<IUnknown> proxyStub;
		winrt::check_hresult(DLLGETCLASSOBJECT_ENTRY(PROXY_CLSID_IS, winrt::guid_of<decltype(proxyStub)::type>(), proxyStub.put_void()));
		winrt::check_hresult(CoRegisterClassObject(PROXY_CLSID_IS, proxyStub.get(), CLSCTX_INPROC_SERVER, REGCLS_MULTIPLEUSE, &s_ProxyStubRegistrationCookie));

		winrt::check_hresult(CoRegisterPSClsid(IID_ITaskbarAppearanceService, PROXY_CLSID_IS));
		winrt::check_hresult(CoRegisterPSClsid(IID_IVersionedApi, PROXY_CLSID_IS));
	}
}

void TaskbarAppearanceService::UninstallProxyStub()
{
	if (s_ProxyStubRegistrationCookie)
	{
		winrt::check_hresult(CoRevokeClassObject(s_ProxyStubRegistrationCookie));
	}
}

winrt::fire_and_forget TaskbarAppearanceService::OnProcessDied()
{
	m_WaitHandle.reset();
	m_Process.reset();

	const auto self_weak = get_weak();
	co_await wil::resume_foreground(m_XamlThreadQueue);

	if (const auto self = self_weak.get())
	{
		self->RestoreAllTaskbarsToDefault();
	}
}

void TaskbarAppearanceService::OnPackageUninstalling(const wam::PackageCatalog&, const wam::PackageUninstallingEventArgs &args)
{
	if (args.Package().Id().FullName() == m_PackageBeingWatched)
	{
		// kill explorer to force the system to restart it, thereby unloading our DLL.
		TerminateProcess(GetCurrentProcess(), 0);
	}
}

void TaskbarAppearanceService::OnPackageUpdating(const wam::PackageCatalog&, const wam::PackageUpdatingEventArgs &args)
{
	if (args.SourcePackage().Id().FullName() == m_PackageBeingWatched)
	{
		// kill explorer to force the system to restart it, thereby unloading our DLL.
		TerminateProcess(GetCurrentProcess(), 0);
	}
}

std::optional<TaskbarAppearanceService::TaskbarInfo> TaskbarAppearanceService::GetTaskbarInfo(HWND taskbar)
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			return info;
		}
	}

	return std::nullopt;
}

void TaskbarAppearanceService::OnTaskbarBackgroundUpdated(const wux::DependencyObject &sender, const wux::DependencyProperty&)
{
	for (auto& [handle, info] : m_Taskbars)
	{
		if (sender == info.background.control && !info.background.originalFill)
		{
			info.background.originalFill = info.background.control.Fill();
		}
	}
}

void TaskbarAppearanceService::OnTaskbarBorderUpdated(const wux::DependencyObject &sender, const wux::DependencyProperty&)
{
	for (auto& [handle, info] : m_Taskbars)
	{
		if (sender == info.border.control && !info.border.originalFill)
		{
			info.border.originalFill = info.border.control.Fill();
		}
	}
}

void TaskbarAppearanceService::RestoreDefaultControlFill(const ControlInfo<wux::Shapes::Shape> &info)
{
	if (info.control && info.originalFill)
	{
		info.control.Fill(info.originalFill);
	}
}

void TaskbarAppearanceService::ProcessWaitCallback(void* parameter, BOOLEAN)
{
	reinterpret_cast<TaskbarAppearanceService *>(parameter)->OnProcessDied();
}
