	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"stack-args.c"
	.section	.text.stack_args,"ax",@progbits
	.globl	stack_args                      ; -- Begin function stack_args
	.type	stack_args,@function
stack_args:                             ; @stack_args
; %bb.0:
	sta	__rc16
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc22
	pha
	lda	__rc23
	pha
	lda	__rc16
	ldy	__rc24
	sty	.Lstack_args_sstk               ; 1-byte Folded Spill
	ldy	__rc25
	sty	.Lstack_args_sstk+1             ; 1-byte Folded Spill
	ldy	#0
	clc
	pha
	lda	__rc0
	sta	__rc18
	lda	__rc1
	sta	__rc19
	pla
	pha
	lda	__rc0
	adc	#1
	sta	__rc20
	lda	__rc1
	adc	#0
	sta	__rc21
	clc
	lda	__rc0
	adc	#2
	sta	__rc22
	lda	__rc1
	adc	#0
	sta	__rc23
	clc
	lda	__rc0
	adc	#3
	sta	__rc24
	lda	__rc1
	adc	#0
	sta	__rc25
	pla
	clc
	adc	__rc2
	sta	__rc2
	txa
	adc	__rc3
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc4
	sta	__rc2
	lda	__rc3
	adc	__rc5
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc6
	sta	__rc2
	lda	__rc3
	adc	__rc7
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc8
	sta	__rc2
	lda	__rc3
	adc	__rc9
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc10
	sta	__rc2
	lda	__rc3
	adc	__rc11
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc12
	sta	__rc2
	lda	__rc3
	adc	__rc13
	sta	__rc3
	lda	__rc2
	clc
	adc	__rc14
	sta	__rc2
	lda	__rc3
	adc	__rc15
	sta	__rc3
	clc
	lda	__rc2
	adc	(__rc18),y
	sta	__rc2
	lda	__rc3
	adc	(__rc20),y
	tax
	lda	__rc2
	clc
	adc	(__rc22),y
	sta	__rc2
	txa
	adc	(__rc24),y
	tax
	lda	__rc2
	sta	__rc16
	ldy	.Lstack_args_sstk+1             ; 1-byte Folded Reload
	sty	__rc25
	ldy	.Lstack_args_sstk               ; 1-byte Folded Reload
	sty	__rc24
	pla
	sta	__rc23
	pla
	sta	__rc22
	pla
	sta	__rc21
	pla
	sta	__rc20
	lda	__rc16
	rts
.Lfunc_end0:
	.size	stack_args, .Lfunc_end0-stack_args
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	2
	.size	.Lstatic_stack, 2

.Lstack_args_sstk = .Lstatic_stack
	.size	.Lstack_args_sstk, 2
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
