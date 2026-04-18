"use client";

import type { ResolvedSource } from "@/lib/types";
import type Hls from "hls.js";
import { useCallback, useEffect, useRef, useState } from "react";

interface CustomVideoPlayerProps {
  source: ResolvedSource;
  poster?: string;
  autoPlay?: boolean;
  startSeconds?: number;
  onTimeUpdate?: (seconds: number, duration: number) => void;
  onEnded?: () => void;
  onError?: (message: string) => void;
}

interface QualityOption {
  index: number;
  height: number;
  label: string;
}

/**
 * Sheicob native HTML5 video player.
 * - Plays HLS via hls.js (with native fallback on Safari)
 * - Plays MP4 directly via <video src>
 * - Surfaces quality levels, play/pause, seekbar, volume, fullscreen, PiP
 */
export default function CustomVideoPlayer({
  source,
  poster,
  autoPlay = true,
  startSeconds = 0,
  onTimeUpdate,
  onEnded,
  onError,
}: CustomVideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const hlsRef = useRef<Hls | null>(null);
  const seededTimeRef = useRef(false);

  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);
  const [qualities, setQualities] = useState<QualityOption[]>([]);
  const [activeQuality, setActiveQuality] = useState<number>(-1); // -1 = auto
  const [showSettings, setShowSettings] = useState(false);
  const [buffering, setBuffering] = useState(false);

  // Attach source (HLS via hls.js or native)
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    seededTimeRef.current = false;

    // Cleanup any prior hls instance
    if (hlsRef.current) {
      hlsRef.current.destroy();
      hlsRef.current = null;
    }

    if (source.format === "hls") {
      // Native HLS (Safari)
      if (video.canPlayType("application/vnd.apple.mpegurl")) {
        video.src = source.url;
        return;
      }

      // hls.js for everyone else — dynamic import keeps initial bundle smaller
      let cancelled = false;
      import("hls.js").then(({ default: HlsCtor }) => {
        if (cancelled) return;
        if (!HlsCtor.isSupported()) {
          onError?.("HLS not supported in this browser");
          return;
        }
        const hls = new HlsCtor({
          enableWorker: true,
          lowLatencyMode: false,
          backBufferLength: 30,
        });
        hlsRef.current = hls;
        hls.loadSource(source.url);
        hls.attachMedia(video);

        hls.on(HlsCtor.Events.MANIFEST_PARSED, (_evt, data) => {
          const opts: QualityOption[] = data.levels
            .map((lvl, i) => ({
              index: i,
              height: lvl.height ?? 0,
              label: lvl.height ? `${lvl.height}p` : `${Math.round((lvl.bitrate ?? 0) / 1000)}kbps`,
            }))
            .filter((q) => q.height > 0)
            .sort((a, b) => b.height - a.height);
          setQualities(opts);
        });

        hls.on(HlsCtor.Events.LEVEL_SWITCHED, (_evt, data) => {
          setActiveQuality(hls.autoLevelEnabled ? -1 : data.level);
        });

        hls.on(HlsCtor.Events.ERROR, (_evt, data) => {
          if (data.fatal) {
            onError?.(`HLS fatal error: ${data.type}/${data.details}`);
          }
        });
      });

      return () => {
        cancelled = true;
      };
    }

    // mp4 / dash fallback to native
    video.src = source.url;
  }, [source, onError]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (hlsRef.current) {
        hlsRef.current.destroy();
        hlsRef.current = null;
      }
    };
  }, []);

  // Wire up native video events
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const onPlay = () => setIsPlaying(true);
    const onPause = () => setIsPlaying(false);
    const onTime = () => {
      setCurrentTime(video.currentTime);
      onTimeUpdate?.(video.currentTime, video.duration || 0);
    };
    const onLoadedMeta = () => {
      setDuration(video.duration || 0);
      if (!seededTimeRef.current && startSeconds > 0 && startSeconds < video.duration) {
        video.currentTime = startSeconds;
        seededTimeRef.current = true;
      }
      if (autoPlay) {
        video.play().catch(() => {
          // Browser blocked autoplay — user will hit play
        });
      }
    };
    const onWaiting = () => setBuffering(true);
    const onCanPlay = () => setBuffering(false);
    const onEndedHandler = () => onEnded?.();
    const onVolume = () => {
      setVolume(video.volume);
      setMuted(video.muted);
    };

    video.addEventListener("play", onPlay);
    video.addEventListener("pause", onPause);
    video.addEventListener("timeupdate", onTime);
    video.addEventListener("loadedmetadata", onLoadedMeta);
    video.addEventListener("waiting", onWaiting);
    video.addEventListener("canplay", onCanPlay);
    video.addEventListener("ended", onEndedHandler);
    video.addEventListener("volumechange", onVolume);

    return () => {
      video.removeEventListener("play", onPlay);
      video.removeEventListener("pause", onPause);
      video.removeEventListener("timeupdate", onTime);
      video.removeEventListener("loadedmetadata", onLoadedMeta);
      video.removeEventListener("waiting", onWaiting);
      video.removeEventListener("canplay", onCanPlay);
      video.removeEventListener("ended", onEndedHandler);
      video.removeEventListener("volumechange", onVolume);
    };
  }, [autoPlay, startSeconds, onTimeUpdate, onEnded]);

  const togglePlay = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) video.play().catch(() => {});
    else video.pause();
  }, []);

  const onSeek = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const video = videoRef.current;
    if (!video) return;
    video.currentTime = Number(e.target.value);
  }, []);

  const onVolumeChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const video = videoRef.current;
    if (!video) return;
    video.volume = Number(e.target.value);
    video.muted = video.volume === 0;
  }, []);

  const toggleMute = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    video.muted = !video.muted;
  }, []);

  const toggleFullscreen = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {});
    } else {
      video.requestFullscreen().catch(() => {});
    }
  }, []);

  const togglePip = useCallback(async () => {
    const video = videoRef.current;
    if (!video) return;
    try {
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      } else if (document.pictureInPictureEnabled) {
        await video.requestPictureInPicture();
      }
    } catch {
      // ignored
    }
  }, []);

  const selectQuality = useCallback((levelIndex: number) => {
    if (!hlsRef.current) return;
    if (levelIndex === -1) {
      hlsRef.current.currentLevel = -1; // auto
    } else {
      hlsRef.current.currentLevel = levelIndex;
    }
    setActiveQuality(levelIndex);
    setShowSettings(false);
  }, []);

  // Keyboard shortcuts
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const video = videoRef.current;
      if (!video) return;
      // Ignore if typing in input/textarea
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA") return;

      switch (e.key) {
        case " ":
        case "k":
          e.preventDefault();
          togglePlay();
          break;
        case "ArrowLeft":
          video.currentTime = Math.max(0, video.currentTime - 5);
          break;
        case "ArrowRight":
          video.currentTime = Math.min(video.duration || 0, video.currentTime + 5);
          break;
        case "ArrowUp":
          video.volume = Math.min(1, video.volume + 0.1);
          break;
        case "ArrowDown":
          video.volume = Math.max(0, video.volume - 0.1);
          break;
        case "m":
          toggleMute();
          break;
        case "f":
          toggleFullscreen();
          break;
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [togglePlay, toggleMute, toggleFullscreen]);

  return (
    <div className="relative w-full h-full bg-black group">
      <video
        ref={videoRef}
        poster={poster}
        playsInline
        crossOrigin={source.proxyRequired ? undefined : "anonymous"}
        className="w-full h-full"
        onClick={togglePlay}
      >
        {source.subtitles.map((sub) => (
          <track
            key={sub.url}
            kind="subtitles"
            src={sub.url}
            srcLang={sub.language}
            label={sub.label ?? sub.language}
            default={sub.isDefault}
          />
        ))}
      </video>

      {/* Buffering spinner */}
      {buffering && (
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
          <div className="w-12 h-12 border-4 border-orange-500 border-t-transparent rounded-full animate-spin" />
        </div>
      )}

      {/* Controls bar */}
      <div className="absolute bottom-0 inset-x-0 bg-gradient-to-t from-black/90 via-black/50 to-transparent px-3 pt-8 pb-2 opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity">
        {/* Seekbar */}
        <input
          type="range"
          min={0}
          max={duration || 0}
          step={0.1}
          value={currentTime}
          onChange={onSeek}
          aria-label="Seek"
          className="w-full h-1 bg-neutral-700 rounded-full appearance-none accent-orange-500 cursor-pointer"
        />
        <div className="flex items-center gap-3 mt-2 text-white text-sm">
          <button onClick={togglePlay} aria-label={isPlaying ? "Pausa" : "Reproducir"} className="min-w-[32px]">
            {isPlaying ? "⏸" : "▶"}
          </button>
          <span className="text-xs tabular-nums">
            {formatTime(currentTime)} / {formatTime(duration)}
          </span>
          <div className="flex items-center gap-1 ml-2">
            <button onClick={toggleMute} aria-label={muted ? "Activar sonido" : "Silenciar"}>
              {muted || volume === 0 ? "🔇" : "🔊"}
            </button>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              onChange={onVolumeChange}
              aria-label="Volumen"
              className="w-16 h-1 accent-orange-500 cursor-pointer"
            />
          </div>
          <div className="ml-auto flex items-center gap-2 relative">
            {qualities.length > 0 && (
              <>
                <button
                  onClick={() => setShowSettings((s) => !s)}
                  aria-label="Calidad"
                  aria-haspopup="menu"
                  aria-expanded={showSettings}
                  className="px-2 py-0.5 text-xs border border-neutral-600 rounded hover:bg-neutral-800"
                >
                  {activeQuality === -1
                    ? "Auto"
                    : qualities.find((q) => q.index === activeQuality)?.label ?? "Auto"}
                </button>
                {showSettings && (
                  <div
                    role="menu"
                    className="absolute bottom-8 right-0 bg-neutral-900 border border-neutral-700 rounded shadow-xl py-1 min-w-[100px] z-20"
                  >
                    <button
                      role="menuitem"
                      onClick={() => selectQuality(-1)}
                      className={`block w-full text-left px-3 py-1.5 text-xs hover:bg-neutral-800 ${
                        activeQuality === -1 ? "text-orange-400" : ""
                      }`}
                    >
                      Auto
                    </button>
                    {qualities.map((q) => (
                      <button
                        key={q.index}
                        role="menuitem"
                        onClick={() => selectQuality(q.index)}
                        className={`block w-full text-left px-3 py-1.5 text-xs hover:bg-neutral-800 ${
                          activeQuality === q.index ? "text-orange-400" : ""
                        }`}
                      >
                        {q.label}
                      </button>
                    ))}
                  </div>
                )}
              </>
            )}
            <button onClick={togglePip} aria-label="Picture in picture" title="PiP">
              ⧉
            </button>
            <button onClick={toggleFullscreen} aria-label="Pantalla completa" title="Pantalla completa">
              ⛶
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function formatTime(sec: number): string {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.floor(sec % 60);
  const pad = (n: number) => n.toString().padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}
