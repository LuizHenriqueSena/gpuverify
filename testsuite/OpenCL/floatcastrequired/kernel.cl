//pass
//--local_size=64 --num_groups=64 --no-inline


__kernel void foo() {

  float x = 2;
  x = exp(x);

}
