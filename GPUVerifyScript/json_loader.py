"""Module for loading JSON files with GPUVerify invocation data."""

from error_codes import ErrorCodes

import json
from collections import namedtuple
import StringIO

class JSONError(Exception):
  """Exception type returned by json_load."""
  def __init__(self, msg):
    self.msg = msg

  def __str__(self):
    return "GPUVerify: JSON_ERROR error ({}): {}" \
      .format(ErrorCodes.JSON_ERROR, self.msg)

def __check_string(data, object_name):
  if not type(data) is unicode:
    raise JSONError(object_name + " expects string")

def __check_hex_string(data, object_name):
  if not type(data) is unicode:
    raise JSONError(object_name + " expects hex string")
  if not data.startswith("0x"):
    raise JSONError(object_name + " expects hex string")

  try:
    int(data, 16)
  except ValueError as e:
    raise JSONError(object_name + " expects hex string")

def __check_positive_number(data, object_name):
  if not type(data) is int:
    raise JSONError(object_name + " expects number > 0")
  if data <= 0:
    raise JSONError(object_name + " expects number > 0")

def __check_array_of_positive_numbers(data, object_name):
  if not type(data) is list:
    raise JSONError(object_name + " expects an array of numbers > 0")

  for i in data:
    if not type(i) is int or i <= 0:
      raise JSONError(object_name + " expects an array of numbers > 0")

def __check_scalar_argument(data):
  for key, value in data.iteritems():
    if key == "value":
      __check_hex_string(value, "Scalar kernel argument value")
      data[key] = value[len("0x"):]

def __check_array_argument(data):
  for key, value in data.iteritems():
    if key == "size":
      __check_positive_number(value, "Array kernel argument size")

def __check_argument(data):
  if not type(data) is dict:
    raise JSONError("kernel arguments need to be objects")
  if not "type" in data:
    raise JSONError("kernel arguments require a 'type' value")

  if data["type"] == "scalar":
    __check_scalar_argument(data)
  elif data["type"] == "array":
    __check_array_argument(data)
  else:
    raise JSONError("Unknown kernel argument type " + str(data["type"]))

def __check_kernel_arguments(data):
  if not type(data) is list:
    raise JSONError("kernel-arguments expects array")

  for i in data:
    __check_argument(i)

def __check_host_api_call(data):
  if not type(data) is dict:
    raise JSONError("api calls need to be objects")
  if not "function-name" in data:
    raise JSONError("api calls require a 'function-name' value")
  if not "compilation-unit" in data:
    raise JSONError("api calls require a 'compilation-unit' value")
  if not "line-number" in data:
    raise JSONError("api calls require a 'line-number' value")

  for key, value in data.iteritems():
    if key == "function-name" or key == "compilation-unit":
      __check_string(value, key)
    elif key == "line-number":
      __check_positive_number(value, key)

def __check_host_api_calls(data):
  if not type(data) is list:
    raise JSONError("host-api-calls expects array")

  for i in data:
    __check_host_api_call(i)

def __extract_defines_and_includes(compiler_flags):
  compiler_flags = compiler_flags.split()
  defines  = []
  includes = []

  i = 0
  while i < len(compiler_flags):
    if compiler_flags[i] == "-D":
      if i + 1 == len(compiler_flags):
        raise JSONError("compiler flag '-D' requires an argument")
      i += 1
      defines.append(compiler_flags[i])
    elif compiler_flags[i].startswith("-D"):
      defines.append(compiler_flags[i][len("-D"):])
    elif compiler_flags[i] == "-I":
      if i + 1 == len(compiler_flags):
        raise JSONError("compiler flag '-I' requires an argument")
      i += 1
      includes.append(compiler_flags[i])
    elif compiler_flags[i].startswith("-I"):
      includes.append(compiler_flags[i][len("-I"):])
    i += 1

  DefinesIncludes = namedtuple("DefinesIncludes", ["defines", "includes"])
  return DefinesIncludes(defines, includes)

def __process_opencl_entry(data):
  if not "kernel-file" in data:
    raise JSONError("kernel invocation entries require a 'kernel-file' value")
  if not "local-size" in data:
    raise JSONError("kernel invocation entries require a 'local-size' value")
  if not "global-size" in data:
    raise JSONError("kernel invocation entries require a 'global-size' value")
  if not "entry-point" in data:
    raise JSONError("kernel invocation entries require an 'entry-point' value")

  for key, value in data.iteritems():
    if key == "language" or key == "kernel-file" or key == "entry-point":
      __check_string(value, key)
    elif key == "local-size" or key == "global-size":
      __check_array_of_positive_numbers(value, key)
    elif key == "compiler-flags":
      __check_string(value, key)
      data[key] = __extract_defines_and_includes(value)
    elif key == "kernel-arguments":
      __check_kernel_arguments(value)
    elif key == "host-api-calls":
      __check_host_api_calls(value)

def __process_kernel_entry(data):
  if not type(data) is dict:
    raise JSONError("kernel invocation entries need to be objects")
  if not "language" in data:
    raise JSONError("kernel invocation entries require a 'language' value")

  # Allow for future extension to CUDA
  if data["language"] == "OpenCL":
    __process_opencl_entry(data)
  else:
    raise JSONError("'language' value needs to be 'OpenCL'")

def __process_json(data):
  if not type(data) is list:
    raise JSONError("Expecting an array of kernel invocation objects")

  for i in data:
    __process_kernel_entry(i)

def json_load(json_filename):
  """Load GPUVerify invocation data from json_file.

  The function either returns a dictionary structured as the JSON file, or
  raises a JSONError in case an error is encountered. It is checked whether
  all required values are present and whether all values are of the right type.

  The function also extracts 'defines' and 'includes' from the compiler-flags
  value and returns a named tuple (defines, includes) instead of a string. In
  the case of scalar argument values '0x' is removed from the beginning of the
  hex string.
  """
  try:
    fp = open(json_filename, "r")
    data = json.load(fp)
    fp.close()
    __process_json(data)
  except IOError as e:
    raise JSONError(str(e))
  except ValueError as e:
    raise JSONError(str(e))
  except JSONError:
    raise

  return data