# MeowSSH interaction guidelines

MeowSSH should optimize common tasks for the fewest deliberate user interactions without crowding the interface.

## Default rule

Show the control needed for the common next action. Hide uncommon, advanced, destructive, or explanatory controls until they are contextually relevant.

A feature existing is not, by itself, a reason to put another button on the current screen.

## Control selection

- Use a primary button for the single action the user is most likely trying to complete now.
- Use a secondary/ghost action for the obvious alternative, such as cancel or defer.
- Use a switch/checkbox only for persistent binary state, not for one-shot commands.
- Use radio/segmented controls only when the user must choose exactly one of a small set of peer options.
- Use a select/menu when choices are numerous or secondary.
- Use disclosure (`details`, an expandable row, or a focused sub-page) for advanced settings and technical detail.
- Put destructive actions in contextual menus/sheets or behind disclosure unless deletion itself is the task.
- Prefer tapping the object itself for its primary action; reserve overflow controls for management actions.

## Interaction budget

- A frequent task should normally take one tap from the screen where its target is already visible.
- Configuration required for every use should be minimized with safe defaults and remembered choices.
- Optional metadata and advanced connection behavior should not block the basic flow.
- Avoid extra confirmation dialogs for reversible or non-destructive actions.
- Confirm irreversible/destructive actions when an accidental tap could cause meaningful loss.

## Density

- Avoid more than one visually dominant action in the same decision surface.
- Do not present several peer buttons when a menu, disclosure, or contextual action is semantically clearer.
- Keep top bars for navigation plus the few actions that are genuinely global to that screen.
- Keep Settings roots navigational and summary-oriented; dense configuration belongs on focused sub-pages.
- On phone widths, controls must not require horizontal scrolling.

## Review questions

For each UI change, ask:

1. What is the most common thing the user came here to do?
2. Can that action require fewer taps without making it dangerous or ambiguous?
3. Is every visible control needed at this moment?
4. Is the control type semantically correct for the state/action it represents?
5. Can advanced or destructive functionality move behind context, disclosure, or overflow?
6. Does the default state avoid unnecessary configuration?
7. Does the phone layout remain readable and uncluttered?

These rules are product constraints, not optional polish. UI tests should lock in important interaction counts and progressive-disclosure behavior where regressions are likely.
