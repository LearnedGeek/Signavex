// Equity-curve chart for Quantback. Single line series via TradingView
// lightweight-charts. Mirrors the price-chart.js scan pattern so it survives
// Blazor enhanced navigation (no inline scripts).
(function () {
    'use strict';

    let instances = {};

    function destroy(containerId) {
        const inst = instances[containerId];
        if (inst) {
            try { inst.chart.remove(); } catch (_) {}
            delete instances[containerId];
        }
    }

    function create(containerId, data, height) {
        destroy(containerId);
        const container = document.getElementById(containerId);
        if (!container || typeof LightweightCharts === 'undefined') return;

        const chart = LightweightCharts.createChart(container, {
            width: container.clientWidth,
            height: height || 320,
            layout: {
                background: { type: 'solid', color: 'transparent' },
                textColor: '#71717a',
                fontFamily: "'Inter', sans-serif",
                fontSize: 11,
            },
            grid: {
                vertLines: { color: '#e4e4e720' },
                horzLines: { color: '#e4e4e720' },
            },
            crosshair: {
                mode: LightweightCharts.CrosshairMode.Normal,
                vertLine: { color: '#71717a40', width: 1, style: 2, labelBackgroundColor: '#4f46e5' },
                horzLine: { color: '#71717a40', width: 1, style: 2, labelBackgroundColor: '#4f46e5' },
            },
            rightPriceScale: { borderColor: '#e4e4e7' },
            timeScale: { borderColor: '#e4e4e7', fixLeftEdge: true, fixRightEdge: true },
            handleScroll: { vertTouchDrag: false },
        });

        const series = chart.addAreaSeries({
            lineColor: '#4f46e5',
            topColor: 'rgba(79, 70, 229, 0.25)',
            bottomColor: 'rgba(79, 70, 229, 0.02)',
            lineWidth: 2,
        });

        if (data && data.length) {
            series.setData(data);
            chart.timeScale().fitContent();
        }

        const handleResize = () => chart.applyOptions({ width: container.clientWidth });
        window.addEventListener('resize', handleResize);

        instances[containerId] = { chart, series, handleResize };
    }

    function initAll() {
        const aliveIds = new Set();
        document.querySelectorAll('[data-equity-chart]').forEach((el) => {
            const id = el.id;
            if (!id) return;
            aliveIds.add(id);
            const cfg = el.getAttribute('data-chart-config');
            const height = parseInt(el.getAttribute('data-chart-height') || '320', 10);
            try {
                const data = JSON.parse(cfg || '[]');
                create(id, data, height);
            } catch (e) {
                console.warn('equity-chart: bad JSON for', id, e);
            }
        });
        // Clean up instances whose containers are gone (DOM morph)
        Object.keys(instances).forEach((id) => {
            if (!aliveIds.has(id)) destroy(id);
        });
    }

    window.equityChart = { create, destroy, initAll };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }

    function registerBlazorHook() {
        if (typeof Blazor === 'undefined' || typeof Blazor.addEventListener !== 'function') {
            setTimeout(registerBlazorHook, 50);
            return;
        }
        Blazor.addEventListener('enhancedload', initAll);
    }
    registerBlazorHook();
})();
