// Tray icon artwork selection: live snapshot ahead of application branding.
#pragma once
namespace atrium::tray {
enum class Artwork { Missing, Snapshot, Application };
// A program's branding must never hide a valid tray-state snapshot.
template<class Snapshot, class Application> Artwork LoadArtwork(Snapshot snapshot, Application application)
{
    if (snapshot())
        return Artwork::Snapshot;
    if (application())
        return Artwork::Application;
    return Artwork::Missing;
}
} // namespace atrium::tray
