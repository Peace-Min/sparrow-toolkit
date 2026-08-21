/*
 * sparrow helper c++ rule validation source
 * enable every implemented c/c++ code and comment rule before running
 */
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>

#ifndef O_NOFOLLOW
#define O_NOFOLLOW 0
#endif

#define VALIDATION_LIMIT 16

enum ValidationState
{
    ValidationReady = 0,
    ValidationRunning = 1,
    ValidationStopped = 2
};

static void validationAction()
{
}

static unsigned long validationLiteralReturn()
{
    return 9;
}

/* TBD return type rule: the returned value is wider than the function result. */
static int validationReturnMismatch(long long wideValue)
{
    return wideValue;
}

static void validationCommentRules()
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

    const char *literal = R"validation(text containing &&, || and // is not a comment)validation";
    (void)literal;
}

static void validationCodeRules(
    int state,
    int ready,
    int valid,
    int forced,
    const char *mode)
{
    unsigned int unsignedValue = 1;
    unsigned long unsignedLongValue = 2;
    unsigned long long unsignedLongLongValue = 3;
    int signedValue = 4;
    long signedLongValue = 5;
    long long signedLongLongValue = 6;
    signed long explicitSignedLongValue = 7;
    signed long long explicitSignedLongLongValue = 8;
    std::size_t itemCount = 9;

    int uninitializedScalar;
    int uninitializedArray[4];
    int *uninitializedPointer;
    char *buffer;

    /* TBD explicit cast rule: narrowing conversion needs review. */
    long long wideValue = 100;
    short narrowedValue = wideValue;

    buffer = static_cast<char *>(std::malloc(itemCount * sizeof(buffer)));
    int descriptor = ::open("sparrow-validation.tmp", O_RDWR);

    if (ready)
        validationAction();

    if (state == ValidationReady)
        validationAction();
    else if (state == ValidationRunning)
        validationAction();

    if (std::strcmp(mode, "cbc") == 0 && ready || forced)
        validationAction();

    if (signedValue == 0)
        validationAction();

    if (10 > signedValue)
        validationAction();

    if (uninitializedPointer != nullptr)
        validationAction();

    for (int index = 0; index < VALIDATION_LIMIT && ready; index++)
        validationAction();

    while (valid || forced)
        validationAction();

    do
        validationAction();
    while (ready && valid);

    switch (state)
    {
    case ValidationReady:
        validationAction();
        break;
    case ValidationRunning:
        validationAction();
        break;
    }

    uninitializedScalar = signedValue;
    uninitializedArray[0] = uninitializedScalar;
    uninitializedPointer = &uninitializedArray[0];

    (void)descriptor;
    (void)explicitSignedLongValue;
    (void)explicitSignedLongLongValue;
    (void)narrowedValue;
    (void)signedLongValue;
    (void)signedLongLongValue;
    (void)uninitializedPointer;
    (void)unsignedLongValue;
    (void)unsignedLongLongValue;
    std::free(buffer);
}

int main()
{
    validationCommentRules();
    validationCodeRules(ValidationReady, 1, 1, 0, "cbc");
    return validationReturnMismatch(static_cast<long long>(validationLiteralReturn()));
}
