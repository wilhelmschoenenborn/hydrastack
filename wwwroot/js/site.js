/* Shared script for content pages (about/faq/gallery/support/shipping).
   index.html has its own equivalents inline — keep this tiny and independent. */
(function () {
  'use strict';

  var btn = document.getElementById('hamburgerBtn');
  var menu = document.getElementById('navMenu');
  var overlay = document.getElementById('navMenuOverlay');
  function toggleNav() {
    var open = menu.classList.contains('open');
    menu.classList.toggle('open', !open);
    overlay.classList.toggle('open', !open);
    btn.classList.toggle('open', !open);
  }
  if (btn && menu && overlay) {
    btn.addEventListener('click', toggleNav);
    overlay.addEventListener('click', toggleNav);
  }

  var observer = new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (e.isIntersecting) e.target.classList.add('visible');
    });
  }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
  document.querySelectorAll('.reveal, .reveal-left, .reveal-right, .reveal-scale, .stagger-children')
    .forEach(function (el) { observer.observe(el); });

  var yearEl = document.getElementById('footerYear');
  if (yearEl) yearEl.textContent = new Date().getFullYear();
})();
