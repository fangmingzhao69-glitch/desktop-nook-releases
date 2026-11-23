#pragma once
#include "GaussianBlurEffect.h"

// Translate the backdrop on the rim only; the center retains the original sample.
struct RefractedBackdrop : winrt::implements<RefractedBackdrop, wge::IGraphicsEffect,
	wge::IGraphicsEffectSource, awge::IGraphicsEffectD2D1Interop>
{
	wge::IGraphicsEffectSource Source{nullptr};
	winrt::hstring Name() const { return L"RimOffset"; }
	void Name(winrt::hstring const&) {}
	HRESULT STDMETHODCALLTYPE GetEffectId(GUID* value) noexcept override
	{ if (!value) return E_POINTER; *value = CLSID_D2D12DAffineTransform; return S_OK; }
	HRESULT STDMETHODCALLTYPE GetNamedPropertyMapping(LPCWSTR, UINT*, awge::GRAPHICS_EFFECT_PROPERTY_MAPPING*) noexcept override
	{ return E_INVALIDARG; }
	HRESULT STDMETHODCALLTYPE GetPropertyCount(UINT* count) noexcept override
	{ if (!count) return E_POINTER; *count = 4; return S_OK; }
	HRESULT STDMETHODCALLTYPE GetProperty(UINT index, ABI::Windows::Foundation::IPropertyValue** value) noexcept try
	{
		if (!value) return E_POINTER;
		wf::IInspectable result{nullptr};
		if (index == D2D1_2DAFFINETRANSFORM_PROP_TRANSFORM_MATRIX)
		{
			const float matrix[]{1, 0, 0, 1, 2.5f, 4.f};
			result = wf::PropertyValue::CreateSingleArray(matrix);
		}
		else if (index == D2D1_2DAFFINETRANSFORM_PROP_BORDER_MODE)
			result = wf::PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD);
		else if (index == D2D1_2DAFFINETRANSFORM_PROP_INTERPOLATION_MODE)
			result = wf::PropertyValue::CreateUInt32(D2D1_2DAFFINETRANSFORM_INTERPOLATION_MODE_LINEAR);
		else if (index == D2D1_2DAFFINETRANSFORM_PROP_SHARPNESS)
			result = wf::PropertyValue::CreateSingle(0.f);
		else return E_BOUNDS;
		*value = result.as<ABI::Windows::Foundation::IPropertyValue>().detach();
		return S_OK;
	}
	catch (...) { return winrt::to_hresult(); }
	HRESULT STDMETHODCALLTYPE GetSourceCount(UINT* count) noexcept override
	{ if (!count) return E_POINTER; *count = 1; return S_OK; }
	HRESULT STDMETHODCALLTYPE GetSource(UINT index, awge::IGraphicsEffectSource** value) noexcept override
	{
		if (!value) return E_POINTER;
		if (index != 0) return E_BOUNDS;
		winrt::copy_to_abi(Source, *reinterpret_cast<void**>(value));
		return S_OK;
	}
};
