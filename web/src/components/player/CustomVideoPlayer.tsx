"use client";

import type { ResolvedSource } from "@/lib/types";
import type Hls from "hls.js";
import {
  Maximize,
  Pause,
  PictureInPicture,
  Play,
  Settings,
  Volume2,
  VolumeX,
} from "lucide-react";
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
  const [controlsVisible, setControlsVisible] = useState(true);
  const hideTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const seekbarRef = useRef<HTMLDivElement | null>(null);
  const [bufferedEnd, setBufferedEnd] = useState(0);
  const [isSeeking, setIsSeeking] = useState(false);

  const showControls = useCallback(() => {
    setControlsVisible(true);
    if (hideTimerRef.current) clearTimeout(hideTimerRef.current);
    hideTimerRef.current = setTimeout(() => setControlsVisible(false), 3000);
  }, []);

  // Always show controls when paused
  useEffect(() => {
    if (!isPlaying) {
      setControlsVisible(true);
      if (hideTimerRef.current) clearTimeout(hideTimerRef.current);
    }
  }, [isPlaying]);

  // Cleanup hide timer
  useEffect(() => {
    return () => {
      if (hideTimerRef.current) clearTimeout(hideTimerRef.current);
    };
  }, []);

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
          // Tuned for proxied HLS: Railway free-tier adds latency per segment,
          // so keep forward buffer modest to avoid flooding the proxy with
          // concurrent requests. Aim for ~5 segments ahead (~30 s).
          maxBufferLength: 30,          // target forward buffer (seconds)
          maxMaxBufferLength: 120,      // absolute cap (seconds)
          backBufferLength: 30,         // keep 30 s behind current time
          maxBufferHole: 0.5,           // tolerate up to 0.5 s gap in buffer
          nudgeOffset: 0.2,             // jump past small holes automatically
          nudgeMaxRetry: 5,
          // Start playback faster: load only 1 fragment before attempting play
          maxBufferSize: 30 * 1000 * 1000,  // 30 MB buffer cap
          startFragPrefetch: true,           // prefetch next frag during current download
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
    const onProgress = () => {
      const buf = video.buffered;
      if (buf.length === 0) { setBufferedEnd(0); return; }
      for (let i = 0; i < buf.length; i++) {
        if (buf.start(i) <= video.currentTime + 0.5 && video.currentTime <= buf.end(i)) {
          setBufferedEnd(buf.end(i));
          return;
        }
      }
      setBufferedEnd(buf.end(buf.length - 1));
    };
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
    video.addEventListener("progress", onProgress);

    return () => {
      video.removeEventListener("play", onPlay);
      video.removeEventListener("pause", onPause);
      video.removeEventListener("timeupdate", onTime);
      video.removeEventListener("loadedmetadata", onLoadedMeta);
      video.removeEventListener("waiting", onWaiting);
      video.removeEventListener("canplay", onCanPlay);
      video.removeEventListener("ended", onEndedHandler);
      video.removeEventListener("volumechange", onVolume);
      video.removeEventListener("progress", onProgress);
    };
  }, [autoPlay, startSeconds, onTimeUpdate, onEnded]);

  const togglePlay = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) video.play().catch(() => {});
    else video.pause();
  }, []);

  const seekToPosition = useCallback((clientX: number) => {
    const bar = seekbarRef.current;
    const video = videoRef.current;
    if (!bar || !video || !duration) return;
    const rect = bar.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    video.currentTime = pct * duration;
  }, [duration]);

  const onSeekBarPointerDown = useCallback((e: React.PointerEvent) => {
    e.preventDefault();
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    setIsSeeking(true);
    seekToPosition(e.clientX);
    showControls();
  }, [seekToPosition, showControls]);

  const onSeekBarPointerMove = useCallback((e: React.PointerEvent) => {
    if (e.pressure === 0) return;
    seekToPosition(e.clientX);
  }, [seekToPosition]);

  const onSeekBarPointerUp = useCallback(() => {
    setIsSeeking(false);
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
    const el = containerRef.current;
    if (!el) return;
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {});
    } else {
      el.requestFullscreen().catch(() => {});
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
    <div
      ref={containerRef}
      className="relative w-full h-full bg-black"
      onMouseMove={showControls}
      onTouchStart={showControls}
    >
      <video
        ref={videoRef}
        poster={poster}
        playsInline
        crossOrigin={source.proxyRequired ? undefined : "anonymous"}
        className="w-full h-full"
        onClick={togglePlay}
      >
        {source.subtitles?.map((sub) => (
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

      {/* Center play indicator (visible when paused) */}
      {!isPlaying && !buffering && (
        <button
          onClick={togglePlay}
          aria-label="Reproducir"
          className="absolute inset-0 flex items-center justify-center group/play"
        >
          <span className="w-20 h-20 rounded-full bg-orange-500/90 flex items-center justify-center shadow-2xl transition-transform group-hover/play:scale-110">
            <Play className="w-10 h-10 text-white fill-white translate-x-0.5" />
          </span>
        </button>
      )}

      {/* Buffering spinner + text */}
      {buffering && (
        <div className="absolute inset-0 flex flex-col items-center justify-center pointer-events-none gap-2">
          <div className="w-12 h-12 border-4 border-orange-500 border-t-transparent rounded-full animate-spin" />
          <p className="text-sm text-neutral-300">Cargando…</p>
        </div>
      )}

      {/* Controls bar — visible on hover/touch, always visible when paused */}
      <div className={`absolute bottom-0 inset-x-0 bg-gradient-to-t from-black/95 via-black/60 to-transparent px-4 pt-10 pb-3 transition-opacity ${controlsVisible || !isPlaying || isSeeking ? "opacity-100" : "opacity-0 pointer-events-none"}`}>
        {/* Seekbar with buffer indicator */}
        <div
          ref={seekbarRef}
          className="group/seek relative w-full h-5 flex items-center cursor-pointer select-none touch-none"
          onPointerDown={onSeekBarPointerDown}
          onPointerMove={onSeekBarPointerMove}
          onPointerUp={onSeekBarPointerUp}
          role="slider"
          aria-label="Seek"
          aria-valuemin={0}
          aria-valuemax={duration || 0}
          aria-valuenow={currentTime}
          tabIndex={0}
          onKeyDown={(e) => {
            const video = videoRef.current;
            if (!video) return;
            if (e.key === "ArrowLeft") { e.preventDefault(); e.stopPropagation(); video.currentTime = Math.max(0, video.currentTime - 5); }
            if (e.key === "ArrowRight") { e.preventDefault(); e.stopPropagation(); video.currentTime = Math.min(duration, video.currentTime + 5); }
          }}
        >
          <div className="absolute inset-x-0 h-1 rounded-full bg-neutral-600/70 group-hover/seek:h-1.5 transition-all">
            {/* Buffered */}
            <div
              className="absolute h-full bg-neutral-400/50 rounded-full transition-[width] duration-300"
              style={{ width: duration > 0 ? `${(bufferedEnd / duration) * 100}%` : "0%" }}
            />
            {/* Played */}
            <div
              className="absolute h-full bg-orange-500 rounded-full"
              style={{ width: duration > 0 ? `${(currentTime / duration) * 100}%` : "0%" }}
            />
          </div>
          {/* Thumb */}
          <div
            className={`absolute w-3.5 h-3.5 bg-orange-500 rounded-full shadow-md -translate-x-1/2 pointer-events-none transition-opacity ${
              isSeeking ? "opacity-100 scale-125" : "opacity-0 group-hover/seek:opacity-100"
            }`}
            style={{ left: duration > 0 ? `${(currentTime / duration) * 100}%` : "0%" }}
          />
        </div>
        <div className="flex items-center gap-3 mt-2.5 text-white text-sm">
          <button
            onClick={togglePlay}
            aria-label={isPlaying ? "Pausa" : "Reproducir"}
            className="p-1.5 rounded hover:bg-white/10 transition-colors"
          >
            {isPlaying ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5 fill-white" />}
          </button>
          <span className="text-xs tabular-nums text-neutral-200">
            {formatTime(currentTime)} / {formatTime(duration)}
          </span>
          <div className="flex items-center gap-1 ml-1 group/vol">
            <button
              onClick={toggleMute}
              aria-label={muted ? "Activar sonido" : "Silenciar"}
              className="p-1.5 rounded hover:bg-white/10 transition-colors"
            >
              {muted || volume === 0 ? (
                <VolumeX className="w-5 h-5" />
              ) : (
                <Volume2 className="w-5 h-5" />
              )}
            </button>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              onChange={onVolumeChange}
              aria-label="Volumen"
              className="w-0 group-hover/vol:w-20 h-1 accent-orange-500 cursor-pointer transition-all"
            />
          </div>
          <div className="ml-auto flex items-center gap-1 relative">
            {qualities.length > 0 && (
              <>
                <button
                  onClick={() => setShowSettings((s) => !s)}
                  aria-label="Calidad"
                  aria-haspopup="menu"
                  aria-expanded={showSettings}
                  className="flex items-center gap-1.5 px-2.5 py-1.5 rounded hover:bg-white/10 transition-colors text-xs font-medium"
                >
                  <Settings className="w-4 h-4" />
                  <span className="tabular-nums">
                    {activeQuality === -1
                      ? "Auto"
                      : qualities.find((q) => q.index === activeQuality)?.label ?? "Auto"}
                  </span>
                </button>
                {showSettings && (
                  <div
                    role="menu"
                    className="absolute bottom-10 right-0 bg-neutral-900/95 backdrop-blur border border-neutral-700/60 rounded-lg shadow-2xl py-1 min-w-[120px] z-20"
                  >
                    <button
                      role="menuitem"
                      onClick={() => selectQuality(-1)}
                      className={`block w-full text-left px-3 py-1.5 text-xs hover:bg-white/10 transition-colors ${
                        activeQuality === -1 ? "text-orange-400 font-medium" : ""
                      }`}
                    >
                      Auto
                    </button>
                    {qualities.map((q) => (
                      <button
                        key={q.index}
                        role="menuitem"
                        onClick={() => selectQuality(q.index)}
                        className={`block w-full text-left px-3 py-1.5 text-xs hover:bg-white/10 transition-colors ${
                          activeQuality === q.index ? "text-orange-400 font-medium" : ""
                        }`}
                      >
                        {q.label}
                      </button>
                    ))}
                  </div>
                )}
              </>
            )}
            <button
              onClick={togglePip}
              aria-label="Picture in picture"
              title="Picture in picture"
              className="p-1.5 rounded hover:bg-white/10 transition-colors"
            >
              <PictureInPicture className="w-5 h-5" />
            </button>
            <button
              onClick={toggleFullscreen}
              aria-label="Pantalla completa"
              title="Pantalla completa"
              className="p-1.5 rounded hover:bg-white/10 transition-colors"
            >
              <Maximize className="w-5 h-5" />
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
