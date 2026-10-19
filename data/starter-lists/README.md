# Starter lists

Curated category lists shipped with the product (`educational.json`, `social.json`, `gaming.json`), offered in the setup wizard. Format: `{ "category", "description", "domains": [...] }`, one registrable domain (or a site's host, like `scratch.mit.edu`) per entry, matched with `domain` semantics (domain and subdomains).

The admin app embeds them (`WebFilter.Admin.csproj`, `StarterLists`); the wizard adds the ones the parent keeps to the family's categories, without replacing a category the family already has. `educational` is also what homework mode allows (`FamilyConfig.EducationalCategory`). `StarterListsTests` checks that every entry is a valid, normalized domain and appears once.

These are a starting point picked by hand, not a vetted catalog: review them before shipping.

Not to be confused with the synthetic categories in `tests/simulation/fixtures/family-base.json`, which exist only for tests.
