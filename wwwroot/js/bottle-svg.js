/*
 * HydraBottle — SVG bottle rendering engine.
 * Renders any lid/mid/base combination as premium layered-gradient SVG markup.
 * Used by: configurator preview, product cards, cart thumbs, gallery, community
 * builds, Build Assistant cards, and marketing sections.
 *
 * All gradient/filter ids are namespaced by opts.idPrefix — REQUIRED whenever
 * more than one bottle can appear on a page, or colors will bleed between SVGs.
 *
 * The GEOM table is the single source of truth for module proportions; the
 * Three.js renderer (js/bottle-3d.js) reads the same table so 2D and 3D match.
 */
(function () {
  'use strict';

  var REDUCED_MOTION = window.matchMedia &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* Same presets & color-object shape as the configurator (getColorObj). */
  var COLOR_DATA = {
    stealth:  { name: 'Stealth',  main: ['#3a4550', '#2C3E50'], accent: '#4a5f6e', adapter: '#333D45', swatch: 'linear-gradient(135deg, #2C3E50, #1A252F)' },
    titanium: { name: 'Titanium', main: ['#C8D4DE', '#9EB0BE'], accent: '#A8B8C6', adapter: '#8A9AAA', swatch: 'linear-gradient(135deg, #B8C4CE, #8EAAB8)' },
    arctic:   { name: 'Arctic',   main: ['#4DD8D8', '#2EB8C8'], accent: '#3ECFCF', adapter: '#2A9AA8', swatch: 'linear-gradient(135deg, #3ECFCF, #2AA8D8)' },
    sand:     { name: 'Sand',     main: ['#EEDCC2', '#C8B090'], accent: '#D4C0A0', adapter: '#B0A080', swatch: 'linear-gradient(135deg, #E8D5B7, #C4A882)' },
    ember:    { name: 'Ember',    main: ['#E06468', '#B83438'], accent: '#D45458', adapter: '#983030', swatch: 'linear-gradient(135deg, #D4585C, #A83235)' },
    alpine:   { name: 'Alpine',   main: ['#58B85C', '#308834'], accent: '#48A84C', adapter: '#287028', swatch: 'linear-gradient(135deg, #4CAF50, #2E7D32)' }
  };

  function shadeColor(color, percent) {
    var R = parseInt(color.substring(1, 3), 16);
    var G = parseInt(color.substring(3, 5), 16);
    var B = parseInt(color.substring(5, 7), 16);
    R = Math.min(255, Math.max(0, Math.round(R * (100 + percent) / 100)));
    G = Math.min(255, Math.max(0, Math.round(G * (100 + percent) / 100)));
    B = Math.min(255, Math.max(0, Math.round(B * (100 + percent) / 100)));
    return '#' + R.toString(16).padStart(2, '0') + G.toString(16).padStart(2, '0') + B.toString(16).padStart(2, '0');
  }

  /* Accepts a preset key ('ember'), a hex string ('#8B5CF6'), or an
     already-resolved color object ({main:[c1,c2], accent, adapter}). */
  function resolveColors(c) {
    if (!c) return COLOR_DATA.stealth;
    if (typeof c === 'object' && c.main) {
      return {
        main: c.main,
        accent: c.accent || c.main[0],
        adapter: c.adapter || shadeColor(c.main[1], -25)
      };
    }
    if (typeof c === 'string') {
      if (COLOR_DATA[c]) return COLOR_DATA[c];
      if (c.charAt(0) === '#' && c.length === 7) {
        var darker = shadeColor(c, -25);
        return { main: [c, darker], accent: c, adapter: darker };
      }
    }
    return COLOR_DATA.stealth;
  }

  /* ---- Geometry (viewBox units, viewBox width = 240, bottle axis x = 120) ---- */
  var GEOM = {
    VBW: 240,
    CX: 120,
    BODY_X: 72, BODY_W: 96,
    NARROW_X: 91, NARROW_W: 58,
    GROUND: 506,
    THREAD_H: 10,
    LID_H: 58,
    MID_H: { standard: 150, handle: 150, xl: 205 },
    BASE_H: { standard: 88, xl: 122, heated: 92, narrow: 64 },
    ADAPTER_H: 24,
    STD_TOTAL: 340 /* reference height of a standard bottle, used for px scaling */
  };

  /* Named sizes = pixel height of a *standard* bottle at that size.
     Taller configs (XL mid/base) scale proportionally taller — physically honest. */
  var SIZES = { sm: 96, md: 180, lg: 320, hero: 430 };

  /* ---- SVG part builders ---- */

  function cylGradient(id, c) {
    var c1 = c.main[0], c2 = c.main[1];
    return '<linearGradient id="' + id + '" x1="0" y1="0" x2="1" y2="0">' +
      '<stop offset="0" stop-color="' + shadeColor(c2, -32) + '"/>' +
      '<stop offset="0.16" stop-color="' + shadeColor(c1, 20) + '"/>' +
      '<stop offset="0.3" stop-color="' + c1 + '"/>' +
      '<stop offset="0.62" stop-color="' + c2 + '"/>' +
      '<stop offset="1" stop-color="' + shadeColor(c2, -36) + '"/>' +
      '</linearGradient>';
  }

  function topEdge(x, y, w) {
    return '<rect x="' + (x + 7) + '" y="' + (y + 2) + '" width="' + (w - 14) +
      '" height="2.4" rx="1.2" fill="#FFFFFF" opacity="0.22"/>';
  }

  function bottomEdge(x, yBottom, w) {
    return '<rect x="' + (x + 6) + '" y="' + (yBottom - 3.4) + '" width="' + (w - 12) +
      '" height="2.6" rx="1.3" fill="#000000" opacity="0.14"/>';
  }

  /* Knurled thread band between two modules; colored from the module below. */
  function threadBand(y, c) {
    var s = '<rect x="80" y="' + y + '" width="80" height="' + GEOM.THREAD_H +
      '" rx="2" fill="' + shadeColor(c.main[1], -18) + '"/>';
    var dark = shadeColor(c.main[1], -32), light = shadeColor(c.main[1], 10);
    for (var i = 0; i < 3; i++) {
      s += '<rect x="82" y="' + (y + 1.4 + i * 3) + '" width="76" height="1.9" rx="0.95" fill="' +
        (i % 2 ? light : dark) + '"/>';
    }
    return s;
  }

  /* Returns { svg, topY } — topY is the topmost visual extent incl. arch/spout/knob. */
  function drawLid(type, lidTop, lidBottom, c, p) {
    var acc = c.accent;
    var s = '<path d="M72 ' + lidBottom + ' L72 ' + (lidTop + 18) +
      ' Q72 ' + lidTop + ' 92 ' + lidTop + ' L148 ' + lidTop +
      ' Q168 ' + lidTop + ' 168 ' + (lidTop + 18) + ' L168 ' + lidBottom +
      ' Z" fill="url(#' + p + '-lg)"/>';
    s += '<rect x="88" y="' + (lidTop + 3) + '" width="64" height="2.4" rx="1.2" fill="#FFFFFF" opacity="0.25"/>';
    s += bottomEdge(GEOM.BODY_X, lidBottom, GEOM.BODY_W);
    var topY = lidTop;

    if (type === 'handle') {
      var y0 = lidTop + 7, ctrl = lidTop - 62;
      var d = 'M94 ' + y0 + ' Q120 ' + ctrl + ' 146 ' + y0;
      s += '<path d="' + d + '" fill="none" stroke="' + shadeColor(acc, -24) + '" stroke-width="11.5" stroke-linecap="round"/>';
      s += '<path d="' + d + '" fill="none" stroke="' + acc + '" stroke-width="8" stroke-linecap="round"/>';
      s += '<circle cx="94" cy="' + (y0 + 1) + '" r="5.5" fill="' + shadeColor(acc, -18) + '"/>';
      s += '<circle cx="146" cy="' + (y0 + 1) + '" r="5.5" fill="' + shadeColor(acc, -18) + '"/>';
      topY = lidTop - 34;
    } else if (type === 'straw') {
      s += '<g transform="rotate(-14 120 ' + (lidTop + 6) + ')">' +
        '<rect x="111" y="' + (lidTop - 25) + '" width="18" height="36" rx="8" fill="' + acc + '"/>' +
        '<rect x="114" y="' + (lidTop - 21) + '" width="4" height="26" rx="2" fill="#FFFFFF" opacity="0.28"/>' +
        '<ellipse cx="120" cy="' + (lidTop - 24) + '" rx="8" ry="3.2" fill="' + shadeColor(acc, -38) + '"/>' +
        '</g>';
      s += '<rect x="161" y="' + (lidTop + 18) + '" width="14" height="26" rx="6" fill="' + shadeColor(acc, -22) + '"/>';
      s += '<rect x="161" y="' + (lidTop + 20) + '" width="11" height="22" rx="5" fill="' + acc + '"/>';
      topY = lidTop - 33;
    } else { /* classic */
      s += '<rect x="106" y="' + (lidTop - 11) + '" width="28" height="14" rx="4" fill="' + shadeColor(acc, -15) + '"/>';
      s += '<ellipse cx="120" cy="' + (lidTop - 11) + '" rx="14" ry="4.2" fill="' + acc + '"/>';
      topY = lidTop - 17;
    }
    return { svg: s, topY: topY };
  }

  function drawMid(type, midTop, midH, c, p) {
    var s = '<rect x="72" y="' + midTop + '" width="96" height="' + midH +
      '" rx="10" fill="url(#' + p + '-mg)"/>';
    s += topEdge(GEOM.BODY_X, midTop, GEOM.BODY_W);
    s += bottomEdge(GEOM.BODY_X, midTop + midH, GEOM.BODY_W);
    if (type === 'handle') {
      var hy1 = midTop + 36, hy2 = midTop + 108;
      var d = 'M166 ' + hy1 + ' C204 ' + (hy1 + 4) + ' 204 ' + (hy2 - 4) + ' 166 ' + hy2;
      s += '<path d="' + d + '" fill="none" stroke="' + shadeColor(c.accent, -24) + '" stroke-width="12" stroke-linecap="round"/>';
      s += '<path d="' + d + '" fill="none" stroke="' + c.accent + '" stroke-width="8.5" stroke-linecap="round"/>';
      s += '<rect x="161" y="' + (hy1 - 5) + '" width="10" height="11" rx="3" fill="' + shadeColor(c.accent, -32) + '"/>';
      s += '<rect x="161" y="' + (hy2 - 6) + '" width="10" height="11" rx="3" fill="' + shadeColor(c.accent, -32) + '"/>';
    }
    return s;
  }

  function drawBase(type, baseTop, baseH, adapterTop, c, p, animateHeat) {
    var s = '', i;
    if (type === 'narrow') {
      s += '<polygon points="72,' + adapterTop + ' 168,' + adapterTop + ' 149,' + (adapterTop + GEOM.ADAPTER_H) +
        ' 91,' + (adapterTop + GEOM.ADAPTER_H) + '" fill="url(#' + p + '-ag)"/>';
      s += '<rect x="79" y="' + (adapterTop + 2) + '" width="82" height="2.2" rx="1.1" fill="#FFFFFF" opacity="0.18"/>';
      s += '<rect x="91" y="' + baseTop + '" width="58" height="' + baseH + '" rx="10" fill="url(#' + p + '-bg)"/>';
      s += bottomEdge(GEOM.NARROW_X, GEOM.GROUND, GEOM.NARROW_W);
      for (i = 0; i < 3; i++) {
        s += '<rect x="99" y="' + (baseTop + 18 + i * 8) + '" width="42" height="2" rx="1" fill="#000000" opacity="0.10"/>';
      }
    } else {
      s += '<rect x="72" y="' + baseTop + '" width="96" height="' + baseH + '" rx="12" fill="url(#' + p + '-bg)"/>';
      s += topEdge(GEOM.BODY_X, baseTop, GEOM.BODY_W);
      s += bottomEdge(GEOM.BODY_X, GEOM.GROUND, GEOM.BODY_W);
      var ribTop = type === 'heated' ? baseTop + 14 : baseTop + Math.round(baseH * 0.34);
      var ribs = type === 'xl' ? 4 : 3;
      for (i = 0; i < ribs; i++) {
        s += '<rect x="84" y="' + (ribTop + i * 8) + '" width="72" height="2" rx="1" fill="#000000" opacity="0.10"/>';
      }
      if (type === 'heated') {
        var ringY = GEOM.GROUND - 26;
        var anim = (animateHeat && !REDUCED_MOTION)
          ? '<animate attributeName="opacity" values="0.4;0.85;0.4" dur="2.6s" repeatCount="indefinite"/>' : '';
        s += '<rect x="74" y="' + (ringY - 2) + '" width="92" height="14" rx="7" fill="url(#' + p + '-hg)" filter="url(#' + p + '-glow)" opacity="0.6">' + anim + '</rect>';
        s += '<rect x="78" y="' + ringY + '" width="84" height="10" rx="5" fill="url(#' + p + '-hg)"/>';
        s += '<rect x="82" y="' + (ringY + 1.6) + '" width="76" height="2" rx="1" fill="#FFFFFF" opacity="0.35"/>';
        s += '<circle cx="157" cy="' + (baseTop + 9) + '" r="2.6" fill="#FFB27A"/>';
      }
    }
    return s;
  }

  var HEAT_GRADIENT =
    '<stop offset="0" stop-color="#E2483D"/>' +
    '<stop offset="0.35" stop-color="#FF8A50"/>' +
    '<stop offset="0.55" stop-color="#FF7A45"/>' +
    '<stop offset="1" stop-color="#C93A32"/>';

  function sanitizePrefix(idPrefix) {
    return String(idPrefix || 'hb').replace(/[^a-zA-Z0-9_-]/g, '');
  }

  function sizeToScale(size) {
    var px = typeof size === 'number' ? size : (SIZES[size] || SIZES.md);
    return px / GEOM.STD_TOTAL;
  }

  /*
   * render(config, opts) -> SVG markup string.
   *   config: { lid, mid, base, lidColor, midColor, baseColor }
   *           colors: preset key | '#hex' | resolved color object
   *   opts:   { size:'sm'|'md'|'lg'|'hero'|px, idPrefix (required-by-convention),
   *             shadow:bool, animateHeat:bool, class:string }
   */
  function render(config, opts) {
    config = config || {};
    opts = opts || {};
    var lid = config.lid || 'handle';
    var mid = config.mid || 'standard';
    var base = config.base || 'standard';
    if (!GEOM.MID_H[mid]) mid = 'standard';
    if (!GEOM.BASE_H[base]) base = 'standard';
    if (['handle', 'straw', 'classic'].indexOf(lid) < 0) lid = 'handle';

    var lc = resolveColors(config.lidColor);
    var mc = resolveColors(config.midColor);
    var bc = resolveColors(config.baseColor);
    var p = sanitizePrefix(opts.idPrefix);

    /* Layout: stack up from a fixed ground line. */
    var baseH = GEOM.BASE_H[base];
    var hasAdapter = base === 'narrow';
    var baseTop = GEOM.GROUND - baseH;
    var adapterTop = hasAdapter ? baseTop - GEOM.ADAPTER_H : baseTop;
    var midBottom = adapterTop - GEOM.THREAD_H;
    var midH = GEOM.MID_H[mid];
    var midTop = midBottom - midH;
    var lidBottom = midTop - GEOM.THREAD_H;
    var lidTop = lidBottom - GEOM.LID_H;

    var defs = cylGradient(p + '-lg', lc) + cylGradient(p + '-mg', mc) + cylGradient(p + '-bg', bc);
    if (hasAdapter) {
      defs += '<linearGradient id="' + p + '-ag" x1="0" y1="0" x2="1" y2="0">' +
        '<stop offset="0" stop-color="' + shadeColor(bc.adapter, -25) + '"/>' +
        '<stop offset="0.3" stop-color="' + shadeColor(bc.adapter, 15) + '"/>' +
        '<stop offset="1" stop-color="' + shadeColor(bc.adapter, -32) + '"/>' +
        '</linearGradient>';
    }
    if (base === 'heated') {
      defs += '<linearGradient id="' + p + '-hg" x1="0" y1="0" x2="1" y2="0">' + HEAT_GRADIENT + '</linearGradient>';
      defs += '<filter id="' + p + '-glow" x="-30%" y="-120%" width="160%" height="340%"><feGaussianBlur stdDeviation="4.5"/></filter>';
    }

    var body = '';
    if (opts.shadow) {
      defs += '<filter id="' + p + '-shb" x="-40%" y="-180%" width="180%" height="460%"><feGaussianBlur stdDeviation="4"/></filter>';
      body += '<ellipse cx="120" cy="' + (GEOM.GROUND + 12) + '" rx="' + (hasAdapter ? 54 : 68) +
        '" ry="8" fill="#211E1A" opacity="0.16" filter="url(#' + p + '-shb)"/>';
    }

    body += drawBase(base, baseTop, baseH, adapterTop, bc, p, opts.animateHeat);
    body += threadBand(midBottom, bc);
    body += drawMid(mid, midTop, midH, mc, p);
    body += threadBand(lidBottom, mc);
    var lidPart = drawLid(lid, lidTop, lidBottom, lc, p);
    body += lidPart.svg;

    /* Full-height sheen stripe — the "wet metal" highlight. */
    var sheenTop = lidTop + 6, sheenBottom = GEOM.GROUND - 6;
    body += '<rect x="92" y="' + sheenTop + '" width="8" height="' + (sheenBottom - sheenTop) + '" rx="4" fill="#FFFFFF" opacity="0.10"/>';
    body += '<rect x="103" y="' + sheenTop + '" width="2.5" height="' + (sheenBottom - sheenTop) + '" rx="1.2" fill="#FFFFFF" opacity="0.07"/>';

    /* Crop the viewBox vertically to content so cards aren't mostly air. */
    var minY = lidPart.topY - 10;
    var maxY = opts.shadow ? GEOM.GROUND + 26 : GEOM.GROUND + 6;
    var cropH = maxY - minY;
    var scale = sizeToScale(opts.size);
    var w = Math.round(GEOM.VBW * scale);
    var h = Math.round(cropH * scale);
    var cls = opts.class ? ' class="' + opts.class + '"' : '';

    return '<svg' + cls + ' xmlns="http://www.w3.org/2000/svg" viewBox="0 ' + minY + ' ' + GEOM.VBW + ' ' + cropH +
      '" width="' + w + '" height="' + h + '" role="img" aria-label="HydraStack bottle">' +
      '<defs>' + defs + '</defs>' + body + '</svg>';
  }

  /*
   * renderModule(part, type, colorLike, opts) -> SVG of a single module,
   * tightly cropped. Used for exploded diagrams and option glyphs.
   *   part: 'lid' | 'mid' | 'base'
   */
  function renderModule(part, type, colorLike, opts) {
    opts = opts || {};
    var c = resolveColors(colorLike);
    var p = sanitizePrefix(opts.idPrefix);
    var defs = '', body = '', minY, maxY;

    if (part === 'lid') {
      var lidTop = 60, lidBottom = lidTop + GEOM.LID_H;
      defs += cylGradient(p + '-lg', c);
      var lp = drawLid(type || 'handle', lidTop, lidBottom, c, p);
      body += lp.svg;
      minY = lp.topY - 8; maxY = lidBottom + 6;
    } else if (part === 'mid') {
      var midTop = 40, midH = GEOM.MID_H[type] || 150;
      defs += cylGradient(p + '-mg', c);
      body += drawMid(type || 'standard', midTop, midH, c, p);
      minY = midTop - 8; maxY = midTop + midH + 6;
    } else {
      var t = type || 'standard';
      if (!GEOM.BASE_H[t]) t = 'standard';
      var baseH = GEOM.BASE_H[t];
      var baseTop = GEOM.GROUND - baseH;
      var adapterTop = t === 'narrow' ? baseTop - GEOM.ADAPTER_H : baseTop;
      defs += cylGradient(p + '-bg', c);
      if (t === 'narrow') {
        defs += '<linearGradient id="' + p + '-ag" x1="0" y1="0" x2="1" y2="0">' +
          '<stop offset="0" stop-color="' + shadeColor(c.adapter, -25) + '"/>' +
          '<stop offset="0.3" stop-color="' + shadeColor(c.adapter, 15) + '"/>' +
          '<stop offset="1" stop-color="' + shadeColor(c.adapter, -32) + '"/>' +
          '</linearGradient>';
      }
      if (t === 'heated') {
        defs += '<linearGradient id="' + p + '-hg" x1="0" y1="0" x2="1" y2="0">' + HEAT_GRADIENT + '</linearGradient>';
        defs += '<filter id="' + p + '-glow" x="-30%" y="-120%" width="160%" height="340%"><feGaussianBlur stdDeviation="4.5"/></filter>';
      }
      body += drawBase(t, baseTop, baseH, adapterTop, c, p, opts.animateHeat);
      minY = adapterTop - 8; maxY = GEOM.GROUND + 6;
    }

    var cropH = maxY - minY;
    var scale = sizeToScale(opts.size);
    var cls = opts.class ? ' class="' + opts.class + '"' : '';
    return '<svg' + cls + ' xmlns="http://www.w3.org/2000/svg" viewBox="0 ' + minY + ' ' + GEOM.VBW + ' ' + cropH +
      '" width="' + Math.round(GEOM.VBW * scale) + '" height="' + Math.round(cropH * scale) +
      '" role="img" aria-label="HydraStack ' + part + ' module">' +
      '<defs>' + defs + '</defs>' + body + '</svg>';
  }

  /* Curated lookbook configurations (Gallery page + inspiration). */
  var PRESETS = [
    { key: 'trail-runner',  name: 'Trail Runner',   tagline: 'Light, fast, one-handed sips.',           lid: 'straw',   mid: 'standard', base: 'narrow',   lidColor: 'ember',    midColor: 'ember',    baseColor: 'stealth' },
    { key: 'desk-companion', name: 'Desk Companion', tagline: 'Coffee stays hot until the standup ends.', lid: 'classic', mid: 'standard', base: 'heated',   lidColor: 'stealth',  midColor: 'stealth',  baseColor: 'stealth' },
    { key: 'summit-pack',   name: 'Summit Pack',    tagline: '24oz of alpine air and cold water.',       lid: 'handle',  mid: 'xl',       base: 'xl',       lidColor: 'alpine',   midColor: 'alpine',   baseColor: 'stealth' },
    { key: 'studio-minimal', name: 'Studio Minimal', tagline: 'Monochrome. No noise.',                    lid: 'classic', mid: 'standard', base: 'standard', lidColor: 'titanium', midColor: 'titanium', baseColor: 'titanium' },
    { key: 'desert-mile',   name: 'Desert Mile',    tagline: 'Warm tones for long dry miles.',           lid: 'straw',   mid: 'xl',       base: 'standard', lidColor: 'sand',     midColor: 'sand',     baseColor: 'ember' },
    { key: 'cold-plunge',   name: 'Cold Plunge',    tagline: 'Arctic through and through.',              lid: 'handle',  mid: 'handle',   base: 'xl',       lidColor: 'arctic',   midColor: 'arctic',   baseColor: 'arctic' },
    { key: 'campfire',      name: 'Campfire',       tagline: 'Heated base, ember shell, zero cold tea.', lid: 'handle',  mid: 'standard', base: 'heated',   lidColor: 'ember',    midColor: 'stealth',  baseColor: 'ember' },
    { key: 'commuter',      name: 'Commuter',       tagline: 'Fits the cup holder, carries the day.',    lid: 'straw',   mid: 'standard', base: 'narrow',   lidColor: 'stealth',  midColor: 'titanium', baseColor: 'stealth' },
    { key: 'meadow',        name: 'Meadow',         tagline: 'Alpine greens over soft sand.',            lid: 'classic', mid: 'handle',   base: 'standard', lidColor: 'alpine',   midColor: 'sand',     baseColor: 'alpine' },
    { key: 'night-shift',   name: 'Night Shift',    tagline: 'Stealth stack with a warm glow below.',    lid: 'straw',   mid: 'handle',   base: 'heated',   lidColor: 'stealth',  midColor: 'stealth',  baseColor: 'titanium' }
  ];

  window.HydraBottle = {
    render: render,
    renderModule: renderModule,
    resolveColors: resolveColors,
    shadeColor: shadeColor,
    COLOR_DATA: COLOR_DATA,
    PRESETS: PRESETS,
    SIZES: SIZES,
    GEOM: GEOM
  };
})();
