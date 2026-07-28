const header = document.querySelector("[data-header]");
const menu = document.querySelector("[data-menu]");
const nav = document.querySelector("[data-nav]");

const updateHeader = () => header?.classList.toggle("scrolled", scrollY > 24);
updateHeader();
addEventListener("scroll", updateHeader, { passive: true });

menu?.addEventListener("click", () => {
  const open = !nav.classList.contains("open");
  nav.classList.toggle("open", open);
  menu.setAttribute("aria-expanded", String(open));
});

nav?.addEventListener("click", event => {
  if (event.target.closest("a")) {
    nav.classList.remove("open");
    menu?.setAttribute("aria-expanded", "false");
  }
});

const reveal = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    entry.target.classList.add("visible");
    reveal.unobserve(entry.target);
  }
}, { threshold: 0.12 });

document.querySelectorAll(".reveal").forEach(element => reveal.observe(element));
