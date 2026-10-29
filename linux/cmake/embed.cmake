# Writes OUTPUT as a C++ file whose NAMESPACE::FUNCTION() returns the bytes of INPUT.
file(READ "${INPUT}" content HEX)
string(LENGTH "${content}" length)
math(EXPR size "${length} / 2")
string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," bytes "${content}")
file(WRITE "${OUTPUT}"
"#include <string_view>\n\nnamespace ${NAMESPACE} {\n\nnamespace {\nconstexpr unsigned char kData[] = {${bytes}0};\n}\n\n"
"std::string_view ${FUNCTION}() { return {reinterpret_cast<const char*>(kData), ${size}}; }\n\n}\n")
