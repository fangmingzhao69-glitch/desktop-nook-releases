#pragma once
#include <cstdint>
namespace winrt::TranslucentTB::Xaml::Models::Primitives {
enum class TaskbarState : std::int32_t { Desktop, VisibleWindow, MaximisedWindow, StartOpened, SearchOpened, TaskViewOpened, BatterySaver };
enum class AccentState : std::int32_t { Normal, Opaque, Clear, Blur, Acrylic };
}
