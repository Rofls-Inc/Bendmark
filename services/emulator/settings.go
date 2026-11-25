package main

import (
	"encoding/json"
	"fmt"
	"io"
	"mime"
	"net/http"
	"strconv"
	"time"
)

// Настройки обработки. Длительности имеют тот же формат, что параметры CLI.
type settings struct {
	Mode         string `json:"mode"`
	ServiceTime  string `json:"service_time"`
	Workers      int    `json:"workers"`
	QueueSize    int    `json:"queue_size"`
	SlowDelay    string `json:"slow_delay"`
	Jitter       string `json:"jitter"`
	OutlierRate  string `json:"outlier_rate"`
	OutlierDelay string `json:"outlier_delay"`
	Seed         string `json:"seed"`
}

type labConfiguration struct {
	Settings    settings `json:"settings"`
	CapacityRPS float64  `json:"capacity_rps"`
}

func (cfg config) labConfiguration() labConfiguration {
	var capacity float64
	if cfg.mode != modeHealthy && cfg.serviceTime > 0 {
		capacity = float64(cfg.workers) / cfg.serviceTime.Seconds()
	}
	return labConfiguration{Settings: cfg.settings(), CapacityRPS: capacity}
}

func (cfg config) settings() settings {
	return settings{
		Mode: cfg.mode, ServiceTime: cfg.serviceTime.String(), Workers: cfg.workers, QueueSize: cfg.queueSize,
		SlowDelay: cfg.slowDelay.String(), Jitter: cfg.noise.jitter.String(),
		OutlierRate:  strconv.FormatFloat(cfg.noise.outlierRate, 'g', -1, 64),
		OutlierDelay: cfg.noise.outlierDelay.String(), Seed: strconv.FormatInt(cfg.noise.seed, 10),
	}
}

func (s settings) config() (config, error) {
	switch s.Mode {
	case modeHealthy, modeReject, modeSaturation, modeSlow:
	default:
		return config{}, fmt.Errorf("unsupported mode %q: use healthy, reject, saturation or slow", s.Mode)
	}
	duration, err := time.ParseDuration(s.ServiceTime)
	if err != nil {
		return config{}, fmt.Errorf("invalid -service-time: %w", err)
	}
	if duration < 0 {
		return config{}, fmt.Errorf("-service-time must not be negative")
	}
	if s.Mode != modeHealthy && duration == 0 {
		return config{}, fmt.Errorf("-service-time must be positive in pool modes")
	}
	if s.Workers <= 0 {
		return config{}, fmt.Errorf("-workers must be a positive integer")
	}
	if s.QueueSize <= 0 {
		return config{}, fmt.Errorf("-queue-size must be a positive integer")
	}
	extraDelay, err := time.ParseDuration(s.SlowDelay)
	if err != nil || extraDelay <= 0 {
		return config{}, fmt.Errorf("-slow-delay must be a positive duration, e.g. 5s")
	}
	noise, err := parseNoise(s.Jitter, s.OutlierRate, s.OutlierDelay, s.Seed, duration)
	if err != nil {
		return config{}, err
	}
	return config{
		mode: s.Mode, serviceTime: duration, workers: s.Workers, queueSize: s.QueueSize,
		slowDelay: extraDelay, noise: noise,
	}, nil
}

func (e *emulator) getSettings(w http.ResponseWriter, _ *http.Request) {
	e.mu.Lock()
	current := e.workload.cfg.labConfiguration()
	e.mu.Unlock()
	writeSettings(w, http.StatusOK, current)
}

func (e *emulator) putSettings(w http.ResponseWriter, r *http.Request) {
	mediaType, _, _ := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if mediaType != "application/json" {
		writeSettings(w, http.StatusUnsupportedMediaType, map[string]string{"detail": "Нужен Content-Type: application/json"})
		return
	}
	var desired settings
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&desired); err != nil || decoder.Decode(new(json.RawMessage)) != io.EOF {
		writeSettings(w, http.StatusBadRequest, map[string]string{"detail": "Ожидается один JSON-объект настроек эмулятора."})
		return
	}
	cfg, err := desired.config()
	if err != nil {
		writeSettings(w, http.StatusBadRequest, map[string]string{"detail": err.Error()})
		return
	}

	e.mu.Lock()
	if e.active > 0 {
		e.mu.Unlock()
		writeSettings(w, http.StatusConflict, map[string]string{"detail": "Эмулятор обрабатывает запросы. Повтори после их завершения."})
		return
	}
	current := cfg.labConfiguration()
	if e.workload.cfg.settings() != current.Settings {
		e.workload = newWorkload(cfg)
	}
	e.mu.Unlock()
	writeSettings(w, http.StatusOK, current)
}

func writeSettings(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(payload)
}
