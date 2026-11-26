#ifndef CODERIM_SETTINGS_NAVIGATION_H
#define CODERIM_SETTINGS_NAVIGATION_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct CRSettingsNavigation CRSettingsNavigation;
enum { CR_SETTINGS_GENERAL, CR_SETTINGS_USAGE, CR_SETTINGS_PROVIDERS, CR_SETTINGS_NOTCH,
       CR_SETTINGS_DIAGNOSTICS, CR_SETTINGS_INFORMATION };
enum { CR_SETTINGS_SELECT_CATEGORY, CR_SETTINGS_SELECT_PROVIDER, CR_SETTINGS_SET_DETAIL };
enum { CR_SETTINGS_CREATE_WINDOW, CR_SETTINGS_REATTACH_CONTENT, CR_SETTINGS_RAISE_WINDOW };
typedef struct {
    const uint8_t *provider;
    size_t length;
    uint32_t category;
    bool has_provider, category_changed, provider_changed;
} CRSettingsSelection;

// Owned by one UI session. The returned selection bytes belong to that session
// and must be copied before its next mutation or destruction.
CRSettingsNavigation *cr_settings_navigation_create(void);
void cr_settings_navigation_free(CRSettingsNavigation *state);
CRSettingsSelection cr_settings_navigation_select(CRSettingsNavigation *state, uint32_t action,
    uint32_t category, const uint8_t *provider, size_t length, bool has_provider);
uint32_t cr_settings_navigation_present(CRSettingsNavigation *state, bool has_attached_content);
void cr_settings_navigation_close(CRSettingsNavigation *state);
#endif
