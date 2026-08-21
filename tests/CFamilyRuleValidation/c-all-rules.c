/*
 * sparrow helper c rule validation source
 * enable every implemented c/c++ code and comment rule before running
 */
#include <fcntl.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#ifndef O_NOFOLLOW
#define O_NOFOLLOW 0
#endif

#define VALIDATION_LIMIT 16

enum validation_state
{
    VALIDATION_READY = 0,
    VALIDATION_RUNNING = 1,
    VALIDATION_STOPPED = 2
};

static void validation_action(void)
{
}

static unsigned long validation_literal_return(void)
{
    return 9;
}

/* TBD return type rule: the returned value is wider than the function result. */
static int validation_return_mismatch(long long wide_value)
{
    return wide_value;
}

static void validation_comment_rules(void)
{
    int count = 0; //trailing comment should move above this statement
    //lowercase standalone single line comment
    count++;

    //first paragraph line
    //second paragraph line
    count++;

    /*lowercase one line block comment*/
    count++;

    /*
     * lowercase multi line block comment
     * the closing delimiter must remain unchanged
     */
    count++;

    const char *literal = "text containing &&, || and // is not a comment";
    (void)literal;
}

static void validation_code_rules(
    int state,
    int ready,
    int valid,
    int forced,
    const char *mode)
{
    unsigned int unsigned_value = 1;
    unsigned long unsigned_long_value = 2;
    unsigned long long unsigned_long_long_value = 3;
    int signed_value = 4;
    long signed_long_value = 5;
    long long signed_long_long_value = 6;
    signed long explicit_signed_long_value = 7;
    signed long long explicit_signed_long_long_value = 8;

    int uninitialized_scalar;
    int uninitialized_array[4];
    int *uninitialized_pointer;
    char *buffer;

    /* TBD explicit cast rule: narrowing conversion needs review. */
    long long wide_value = 100;
    short narrowed_value = wide_value;

    buffer = malloc(unsigned_value * sizeof(buffer));
    int descriptor = open("sparrow-validation.tmp", O_RDWR);

    if (ready)
        validation_action();

    if (state == VALIDATION_READY)
        validation_action();
    else if (state == VALIDATION_RUNNING)
        validation_action();

    if (strcmp(mode, "cbc") == 0 && ready || forced)
        validation_action();

    if (signed_value == 0)
        validation_action();

    if (10 > signed_value)
        validation_action();

    for (int index = 0; index < VALIDATION_LIMIT && ready; index++)
        validation_action();

    while (valid || forced)
        validation_action();

    do
        validation_action();
    while (ready && valid);

    switch (state)
    {
    case VALIDATION_READY:
        validation_action();
        break;
    case VALIDATION_RUNNING:
        validation_action();
        break;
    }

    uninitialized_scalar = signed_value;
    uninitialized_array[0] = uninitialized_scalar;
    uninitialized_pointer = &uninitialized_array[0];

    (void)descriptor;
    (void)explicit_signed_long_value;
    (void)explicit_signed_long_long_value;
    (void)narrowed_value;
    (void)signed_long_value;
    (void)signed_long_long_value;
    (void)uninitialized_pointer;
    (void)unsigned_long_value;
    (void)unsigned_long_long_value;
    free(buffer);
}

int main(void)
{
    validation_comment_rules();
    validation_code_rules(VALIDATION_READY, 1, 1, 0, "cbc");
    return validation_return_mismatch((long long)validation_literal_return());
}
