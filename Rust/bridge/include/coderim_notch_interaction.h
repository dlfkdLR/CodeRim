#ifndef CODERIM_NOTCH_INTERACTION_H
#define CODERIM_NOTCH_INTERACTION_H
#include <stdint.h>
#include <stdbool.h>

// One zero-initialized session per notch window. Zero timer IDs mean no timer.
typedef struct {
    double peek_until;
    uint64_t serial, fold_timer, peek_timer, hover_timer;
    bool expanded, pinned, always_on, menu_open, hidden;
} CRNotchInteraction;

enum { CR_NOTCH_ENTER, CR_NOTCH_LEAVE, CR_NOTCH_FOLD_FIRED, CR_NOTCH_PEEK,
       CR_NOTCH_PEEK_ENDED_OUTSIDE, CR_NOTCH_PEEK_ENDED_INSIDE, CR_NOTCH_FOCUSED,
       CR_NOTCH_ALWAYS, CR_NOTCH_ON_HOVER, CR_NOTCH_HIDDEN, CR_NOTCH_TOGGLE_PIN,
       CR_NOTCH_MENU_OPEN, CR_NOTCH_MENU_CLOSE, CR_NOTCH_EDGE_FOLD,
       CR_NOTCH_EDGE_OPEN, CR_NOTCH_STOP, CR_NOTCH_CANCEL_HOVER_CLEAR,
       CR_NOTCH_QUEUE_HOVER_CLEAR, CR_NOTCH_HOVER_CLEAR_FIRED };
enum { CR_NOTCH_CLEAR_HOVER = 1 };

// time is the current reference date on leave, or the absolute deadline for a new peek.
// Fired timer IDs are the IDs captured when the native scheduler queued the callback.
uint32_t cr_notch_interact(CRNotchInteraction *session, uint32_t event, double time, uint64_t timer_id);
#endif
